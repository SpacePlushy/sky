using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Sky.CelesTrak.Tests;

/// <summary>
/// The cache policy that keeps Sky within CelesTrak's usage rules
/// (https://celestrak.org/usage-policy.php): GP data updates every 2 hours, a repeat download
/// inside that window returns 403, and 50 errors in 2 hours firewalls the client's IP address.
/// Every test asserts the exact requests made, so "no request" is checked directly.
/// </summary>
public sealed class GpCacheTests : IDisposable
{
    private const string StationsUrl = "https://celestrak.org/NORAD/elements/gp.php?GROUP=stations&FORMAT=JSON";
    private static readonly string Stations = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations-2026-09-24.json"));

    // Newest element epoch in the fixture is 2026-09-24; start the clock shortly after the download.
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 23, 10, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sky-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ScriptedHandler _server = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private GpCache NewCache() => new(_directory, new CelesTrakClient(new HttpClient(_server)), _clock);

    [Fact]
    public async Task First_request_downloads_the_group_once_and_stores_it()
    {
        _server.Respond(HttpStatusCode.OK, Stations);

        var result = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Equal([new Uri(StationsUrl)], _server.Requests);
        Assert.Equal(22, result.Records.Count);
        Assert.Equal(GpDataSource.Downloaded, result.Source);
        Assert.Equal(Start, result.DownloadedUtc);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Data_under_6_hours_old_is_served_from_disk_without_a_request()
    {
        _server.Respond(HttpStatusCode.OK, Stations);
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromHours(5.9));
        var result = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Single(_server.Requests);
        Assert.Equal(GpDataSource.Cache, result.Source);
        Assert.Equal(22, result.Records.Count);
        Assert.Equal(Start, result.DownloadedUtc);
    }

    [Fact]
    public async Task Data_over_6_hours_old_is_downloaded_again()
    {
        _server.Respond(HttpStatusCode.OK, Stations).Respond(HttpStatusCode.OK, Stations);
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromHours(6.1));
        var result = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(GpDataSource.Downloaded, result.Source);
        Assert.Equal(Start + TimeSpan.FromHours(6.1), result.DownloadedUtc);
    }

    [Fact]
    public async Task A_forced_refresh_still_waits_2_hours_after_the_last_request()
    {
        _server.Respond(HttpStatusCode.OK, Stations).Respond(HttpStatusCode.OK, Stations);
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromMinutes(119));
        var tooSoon = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken, forceRefresh: true);

        Assert.Single(_server.Requests);
        Assert.Equal(GpDataSource.Cache, tooSoon.Source);
        Assert.Contains(tooSoon.Warnings, w => w.Contains("2 hours", StringComparison.Ordinal));

        _clock.Advance(TimeSpan.FromMinutes(1));
        var allowed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken, forceRefresh: true);

        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(GpDataSource.Downloaded, allowed.Source);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GP data has not updated since your last successful download of GROUP=stations at 2026-09-24 23:10:00 UTC. Data is updated once every 2 hours.")]
    [InlineData(HttpStatusCode.NotFound, "Not Found")]
    [InlineData(HttpStatusCode.MovedPermanently, "Moved")]
    [InlineData(HttpStatusCode.InternalServerError, "Server error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Maintenance")]
    public async Task Any_non_200_response_stops_all_requests_for_that_group_until_a_person_clears_it(HttpStatusCode status, string body)
    {
        _server.Respond(HttpStatusCode.OK, Stations).Respond(status, body);
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        string saved = await File.ReadAllTextAsync(Path.Combine(_directory, "stations.json"), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(7));

        var failed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Equal(saved, await File.ReadAllTextAsync(Path.Combine(_directory, "stations.json"), TestContext.Current.CancellationToken));

        // The good data is still served, and the response is reported word for word.
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(GpDataSource.Cache, failed.Source);
        Assert.Equal(22, failed.Records.Count);
        Assert.Contains(failed.Warnings, w => w.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) && w.Contains(body, StringComparison.Ordinal));

        // No automatic retry, however long it has been, in this process or a new one.
        _clock.Advance(TimeSpan.FromDays(3));
        var stillBlocked = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken, forceRefresh: true);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Contains(stillBlocked.Warnings, w => w.Contains(body, StringComparison.Ordinal));

        // A person clears the block; the next request is allowed.
        _server.Respond(HttpStatusCode.OK, Stations);
        var cache = NewCache();
        await cache.ClearBlockAsync("stations", TestContext.Current.CancellationToken);
        var recovered = await cache.GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Equal(3, _server.Requests.Count);
        Assert.Equal(GpDataSource.Downloaded, recovered.Source);
    }

    [Theory]
    [InlineData("No GP data found")]
    [InlineData("[]")]
    [InlineData("<html>Service moved</html>")]
    public async Task A_200_response_without_valid_element_sets_is_treated_as_an_error(string body)
    {
        _server.Respond(HttpStatusCode.OK, Stations).Respond(HttpStatusCode.OK, body);
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        string before = await File.ReadAllTextAsync(Path.Combine(_directory, "stations.json"), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(7));

        var result = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Equal(22, result.Records.Count);
        Assert.Equal(GpDataSource.Cache, result.Source);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_directory, "stations.json"), TestContext.Current.CancellationToken));

        _clock.Advance(TimeSpan.FromDays(1));
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task Network_failures_back_off_from_2_hours_doubling_to_a_24_hour_cap()
    {
        // No HTTP response at all, so CelesTrak never answered; retrying later is allowed.
        _server.FailWithNetworkError();
        var first = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Empty(first.Records);
        Assert.Equal(GpDataSource.None, first.Source);
        Assert.NotEmpty(first.Warnings);

        // Waits of 2, 4, 8, 16, 24, 24 hours after each consecutive failure.
        foreach (double hours in new[] { 2.0, 4.0, 8.0, 16.0, 24.0, 24.0 })
        {
            int requestsSoFar = _server.Requests.Count;
            _clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromMinutes(1));
            await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
            Assert.Equal(requestsSoFar, _server.Requests.Count);

            _clock.Advance(TimeSpan.FromMinutes(1));
            _server.FailWithNetworkError();
            await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
            Assert.Equal(requestsSoFar + 1, _server.Requests.Count);
        }
    }

    [Fact]
    public async Task A_success_after_network_failures_resets_the_backoff()
    {
        _server.FailWithNetworkError().FailWithNetworkError().Respond(HttpStatusCode.OK, Stations).Respond(HttpStatusCode.OK, Stations);
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(2));
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(4));
        var recovered = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Equal(GpDataSource.Downloaded, recovered.Source);

        // Exactly 2 hours later a forced refresh is allowed only if the backoff (4 h after two
        // failures) was reset by the success.
        _clock.Advance(TimeSpan.FromHours(2));
        var refreshed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken, forceRefresh: true);

        Assert.Equal(4, _server.Requests.Count);
        Assert.Equal(GpDataSource.Downloaded, refreshed.Source);
    }

    [Fact]
    public async Task Warns_when_the_newest_element_set_is_more_than_3_days_old()
    {
        _server.Respond(HttpStatusCode.OK, Stations);
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)); // newest epoch is 2026-09-24

        var result = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("days old", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_interrupted_mid_request_still_counts_toward_the_2_hour_rule()
    {
        // The request may have reached CelesTrak before the interruption (Ctrl+C, crash, debugger
        // stop), so the attempt must be on disk before the request is sent.
        _server.HangUntilCancelled();
        string? stateWhenSent = null;
        _server.OnRequest = _ => stateWhenSent = File.ReadAllText(Path.Combine(_directory, "stations.state.json"));
        using var interrupt = new CancellationTokenSource();
        var interrupted = NewCache().GetGroupAsync("stations", interrupt.Token);
        while (_server.Requests.Count == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await interrupt.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        _server.OnRequest = null;
        Assert.NotNull(stateWhenSent);
        Assert.Contains("2026-09-24T23:10:00", stateWhenSent, StringComparison.Ordinal); // the attempt, already on disk

        _clock.Advance(TimeSpan.FromMinutes(119));
        var tooSoon = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Single(_server.Requests);
        Assert.Equal(GpDataSource.None, tooSoon.Source);

        _server.Respond(HttpStatusCode.OK, Stations);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var allowed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(GpDataSource.Downloaded, allowed.Source);
    }

    [Fact]
    public async Task An_unreadable_state_file_blocks_the_group_until_a_person_clears_it()
    {
        // The unreadable file might have recorded a block (a CelesTrak error that needs a person),
        // so the safe reading is "blocked".
        await WriteUnreadableStateAsync(writtenAt: Start - TimeSpan.FromHours(1));

        var blocked = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(2));
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken, forceRefresh: true);

        Assert.Empty(_server.Requests);
        Assert.Contains(blocked.Warnings, w => w.Contains("state", StringComparison.OrdinalIgnoreCase));

        _server.Respond(HttpStatusCode.OK, Stations);
        var cache = NewCache();
        await cache.ClearBlockAsync("stations", TestContext.Current.CancellationToken);
        var recovered = await cache.GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Single(_server.Requests);
        Assert.Equal(GpDataSource.Downloaded, recovered.Source);
    }

    [Fact]
    public async Task After_clearing_an_unreadable_state_the_2_hour_rule_counts_from_its_last_write()
    {
        // The state file is rewritten on every attempt, so its modification time is no earlier than
        // the last request.
        await WriteUnreadableStateAsync(writtenAt: Start - TimeSpan.FromMinutes(30));
        var cache = NewCache();
        await cache.ClearBlockAsync("stations", TestContext.Current.CancellationToken);

        await cache.GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Empty(_server.Requests);

        _server.Respond(HttpStatusCode.OK, Stations);
        _clock.Advance(TimeSpan.FromMinutes(90));
        var allowed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Single(_server.Requests);
        Assert.Equal(GpDataSource.Downloaded, allowed.Source);
    }

    private async Task WriteUnreadableStateAsync(DateTimeOffset writtenAt)
    {
        Directory.CreateDirectory(_directory);
        string statePath = Path.Combine(_directory, "stations.state.json");
        await File.WriteAllTextAsync(statePath, "{ not json", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(statePath, writtenAt.UtcDateTime);
    }

    [Fact]
    public async Task A_non_200_whose_body_fails_to_arrive_still_blocks_the_group()
    {
        // CelesTrak answered; only the body was lost. That is a non-200 answer, not a network failure.
        _server.RespondWithBrokenBody(HttpStatusCode.Forbidden);

        var result = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(1));
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Single(_server.Requests);
        Assert.Contains(result.Warnings, w => w.Contains("403", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_200_whose_body_fails_to_arrive_is_a_network_failure()
    {
        _server.RespondWithBrokenBody(HttpStatusCode.OK).Respond(HttpStatusCode.OK, Stations);

        var failed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(2));
        var retried = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Contains(failed.Warnings, w => w.Contains("Could not reach CelesTrak", StringComparison.Ordinal));
        Assert.Equal(GpDataSource.Downloaded, retried.Source);
    }

    [Fact]
    public async Task Any_celestrak_answer_resets_the_network_backoff()
    {
        // Two network failures set a 4 hour wait. A 403 is an answer, so after a person clears the
        // block the normal 2-hour rule applies, not the old backoff.
        _server.FailWithNetworkError().FailWithNetworkError().Respond(HttpStatusCode.Forbidden, "Forbidden");
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(2));
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(4));
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Equal(3, _server.Requests.Count);

        var cache = NewCache();
        await cache.ClearBlockAsync("stations", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(2));
        _server.Respond(HttpStatusCode.OK, Stations);
        var recovered = await cache.GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Equal(4, _server.Requests.Count);
        Assert.Equal(GpDataSource.Downloaded, recovered.Source);
    }

    [Fact]
    public async Task Timestamps_from_the_future_do_not_lock_the_group_out()
    {
        // The clock was ahead when the state was written, then corrected. The real request happened
        // at or before the real present, so the 2-hour rule counts from now.
        Directory.CreateDirectory(_directory);
        string future = (Start + TimeSpan.FromDays(30)).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "stations.state.json"),
            $$"""{"LastAttemptUtc":"{{future}}","DownloadedUtc":"{{future}}","NetworkFailures":0,"Blocked":null}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "stations.json"), Stations, TestContext.Current.CancellationToken);

        var now = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Empty(_server.Requests);
        Assert.Contains(now.Warnings, w => w.Contains("future", StringComparison.Ordinal));

        _server.Respond(HttpStatusCode.OK, Stations);
        _clock.Advance(TimeSpan.FromHours(2));
        var later = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        Assert.Single(_server.Requests);
        Assert.Equal(GpDataSource.Downloaded, later.Source);
    }

    [Fact]
    public async Task Concurrent_requests_for_a_group_share_one_download()
    {
        // The response is held for 200 ms: without the lock, all three callers would find no data and
        // send their own requests while the first one waits.
        _server.Respond(HttpStatusCode.OK, Stations);
        _server.Delay = TimeSpan.FromMilliseconds(200);
        var cache = NewCache();

        var results = await Task.WhenAll(
            cache.GetGroupAsync("stations", TestContext.Current.CancellationToken),
            cache.GetGroupAsync("stations", TestContext.Current.CancellationToken),
            cache.GetGroupAsync("stations", TestContext.Current.CancellationToken));

        Assert.Single(_server.Requests);
        Assert.All(results, r => Assert.Equal(22, r.Records.Count));
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("stations&FORMAT=TLE")]
    [InlineData("")]
    [InlineData("STATIONS ")]
    public async Task Rejects_group_names_that_are_not_plain_identifiers(string group)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewCache().GetGroupAsync(group, TestContext.Current.CancellationToken));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void The_http_handler_does_not_follow_redirects()
    {
        // CelesTrak counts a 301 as an error toward its firewall limit, and the .com domain
        // redirects. Following it silently would hide the mistake, so a 301 must surface.
        using var handler = CelesTrakClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Two_caches_sharing_a_folder_make_one_request_between_them()
    {
        // The CLI and the dashboard are separate processes that can share one cache folder. Two
        // instances stand in for them here: the file lock must stop the second from requesting while
        // the first's request is in flight, and it must then find the fresh data and not request.
        _server.Respond(HttpStatusCode.OK, Stations).Delay = TimeSpan.FromMilliseconds(300);
        using var first = NewCache();
        using var second = NewCache();

        var results = await Task.WhenAll(
            first.GetGroupAsync("stations", TestContext.Current.CancellationToken),
            second.GetGroupAsync("stations", TestContext.Current.CancellationToken));

        Assert.Single(_server.Requests);
        Assert.All(results, r => Assert.Equal(22, r.Records.Count));
        Assert.Contains(results, r => r.Source == GpDataSource.Downloaded);
        Assert.Contains(results, r => r.Source == GpDataSource.Cache);
    }

    [Fact]
    public async Task Offline_mode_serves_the_cache_and_never_requests_even_when_data_is_due()
    {
        _server.Respond(HttpStatusCode.OK, Stations);
        await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);
        var stateBefore = File.ReadAllText(Path.Combine(_directory, "stations.state.json"));

        _clock.Advance(TimeSpan.FromDays(3));
        using var offline = new GpCache(_directory, new CelesTrakClient(new HttpClient(_server)), _clock, offline: true);
        var result = await offline.GetGroupAsync("stations", TestContext.Current.CancellationToken, forceRefresh: true);

        Assert.Single(_server.Requests); // only the online download above
        Assert.Equal(GpDataSource.Cache, result.Source);
        Assert.Equal(22, result.Records.Count);
        Assert.Equal(stateBefore, File.ReadAllText(Path.Combine(_directory, "stations.state.json")));
    }

    [Fact]
    public async Task Offline_mode_with_nothing_cached_says_so_and_writes_nothing()
    {
        using var offline = new GpCache(_directory, new CelesTrakClient(new HttpClient(_server)), _clock, offline: true);

        var result = await offline.GetGroupAsync("stations", TestContext.Current.CancellationToken);

        Assert.Empty(_server.Requests);
        Assert.Empty(result.Records);
        Assert.Equal(GpDataSource.None, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("Offline mode", StringComparison.Ordinal));
        Assert.False(Directory.Exists(_directory), "Offline mode must not create or write the cache folder.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => offline.ClearBlockAsync("stations", TestContext.Current.CancellationToken));
    }
}
