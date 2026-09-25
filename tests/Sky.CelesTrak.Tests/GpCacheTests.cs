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
        _clock.Advance(TimeSpan.FromHours(7));

        var failed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

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
        cache.ClearBlock("stations");
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

        _clock.Advance(TimeSpan.FromHours(6.1)); // the normal 6-hour refresh, not a long backoff
        var refreshed = await NewCache().GetGroupAsync("stations", TestContext.Current.CancellationToken);

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
    public async Task Concurrent_requests_for_a_group_share_one_download()
    {
        _server.Respond(HttpStatusCode.OK, Stations);
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
}
