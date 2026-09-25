using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sky.CelesTrak;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Api.Tests;

/// <summary>
/// What the Milestone 3 code review found: aborted requests, foreign Host headers, startup errors,
/// instants, ?at= bounds, and the element-set choice across groups.
/// </summary>
public sealed class RobustnessTests
{
    private static readonly TopocentricFrame Phoenix = new(new Geodetic(33.4478, -112.0972, 0.331));

    [Fact]
    public async Task An_aborted_request_does_not_cancel_the_download_it_started()
    {
        // Online, with an empty cache: the first request starts a download that takes a second. The
        // browser gives up after 0.2 s. The download must still finish and serve the next request,
        // with one CelesTrak request in all. Before the fix, the abort cancelled it after the attempt
        // was recorded, and the 2-hour rule left no data at all.
        var celestrak = new SlowCelesTrak(TimeSpan.FromSeconds(1));
        using var host = new ApiHost(seed: false, online: celestrak);
        using var client = host.CreateClient();

        using (var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            abort.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(new Uri("/api/satellites", UriKind.Relative), abort.Token));
        }

        using var response = await client.GetAsync(new Uri("/api/satellites", UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(22, JsonDocument.Parse(body).RootElement.GetArrayLength());
        Assert.Equal(1, celestrak.Requests);
    }

    [Fact]
    public async Task A_foreign_host_header_is_refused()
    {
        // A page that rebinds its own DNS name to 127.0.0.1 sends its name as Host; it must not be
        // able to read the observer's location.
        using var host = new ApiHost();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/config", UriKind.Relative));
        request.Headers.Host = "attacker.example";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_settings_stop_the_api_with_the_message_and_no_crash()
    {
        var start = new ProcessStartInfo("dotnet", "Sky.Api.dll")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment["SKY_Observer__TimeZone"] = "Not/AZone";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        string all = await error + await output;
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Observer:TimeZone", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_value_belongs_to_the_millisecond_it_reports()
    {
        // A clock with sub-millisecond ticks, and a thinned track whose step would not be a whole
        // number of milliseconds: every reported value must be the core's value at the reported time.
        using var host = new ApiHost(start: ApiHost.Start.AddTicks(1234));
        using var client = host.CreateClient();
        var propagator = Sgp4Propagator.Create(Iss().Elements);

        var now = await GetJson(client, "/api/satellites/25544/now");
        var t = Time(now);
        Assert.Equal(0, t.UtcTicks % TimeSpan.TicksPerMillisecond);
        var subpoint = Wgs84.FromEcef(EarthRotation.TemeToEcef(propagator.Propagate(t).State, t).Position);
        Assert.Equal(subpoint.LatitudeDegrees, now.GetProperty("position").GetProperty("latitudeDeg").GetDouble(), 1e-12);
        Assert.Equal(subpoint.LongitudeDegrees, now.GetProperty("position").GetProperty("longitudeDeg").GetDouble(), 1e-12);

        var track = await GetJson(client, "/api/satellites/25544/track?minutes=1234.5678");
        var points = track.GetProperty("points").EnumerateArray().ToList();
        Assert.InRange(points.Count, 900, 1000);
        foreach (var point in points.Where((_, i) => i % 50 == 0))
        {
            var at = Time(point);
            var g = Wgs84.FromEcef(EarthRotation.TemeToEcef(propagator.Propagate(at).State, at).Position);
            Assert.Equal(g.LatitudeDegrees, point.GetProperty("latitudeDeg").GetDouble(), 1e-12);
            Assert.Equal(Visibility.IsSunlit(propagator, at), point.GetProperty("sunlit").GetBoolean());
        }
    }

    [Theory]
    [InlineData("2026-10-26T00:00:00Z")] // 32 days after the epoch
    [InlineData("0001-01-02T00:00:00Z")] // near DateTimeOffset.MinValue
    [InlineData("9999-12-30T00:00:00Z")] // near DateTimeOffset.MaxValue
    public async Task An_at_far_from_the_epoch_is_a_400_not_a_crash(string at)
    {
        using var host = new ApiHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri($"/api/satellites/25544/now?at={Uri.EscapeDataString(at)}", UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid time", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Element_age_warnings_follow_at_not_the_clock()
    {
        using var host = new ApiHost();
        using var client = host.CreateClient();

        var json = await GetJson(client, "/api/satellites/25544/now?at=2026-09-29T04:00:00Z");

        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("days old", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_element_set_is_the_first_group_holding_the_satellite_as_in_the_cli()
    {
        // stations holds the recorded ISS and an older copy; visual holds a newer copy and a
        // satellite of its own. The CLI takes the first group that has the satellite and its newest
        // epoch there, so the answer is the recorded stations ISS, not visual's newer one.
        var recorded = Iss().Elements.Epoch;
        using var host = new ApiHost(seed: false, groups: ["stations", "visual"], seedFiles: folder =>
        {
            var stations = JsonNode.Parse(File.ReadAllText(ApiHost.FixturePath))!.AsArray();
            var iss = stations.First(r => r!["NORAD_CAT_ID"]!.GetValue<long>() == 25544)!;
            var older = iss.DeepClone();
            older["EPOCH"] = recorded.AddDays(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
            stations.Add(older);
            File.WriteAllText(Path.Combine(folder, "stations.json"), stations.ToJsonString());

            var newer = iss.DeepClone();
            newer["EPOCH"] = recorded.AddHours(1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture);
            var own = iss.DeepClone();
            own["NORAD_CAT_ID"] = 99990;
            own["OBJECT_NAME"] = "VISUAL ONLY";
            File.WriteAllText(Path.Combine(folder, "visual.json"), new JsonArray(newer, own).ToJsonString());
        });
        using var client = host.CreateClient();

        var list = (await GetJson(client, "/api/satellites")).EnumerateArray().ToList();
        var issEntry = list.Single(s => s.GetProperty("id").GetInt64() == 25544);
        Assert.Equal("stations", issEntry.GetProperty("group").GetString());
        Assert.Equal(recorded.UtcDateTime, issEntry.GetProperty("epochUtc").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal("visual", list.Single(s => s.GetProperty("id").GetInt64() == 99990).GetProperty("group").GetString());

        var now = await GetJson(client, "/api/satellites/25544/now");
        Assert.Equal(recorded.UtcDateTime, now.GetProperty("satellite").GetProperty("epochUtc").GetDateTime(), TimeSpan.FromMilliseconds(1));
    }

    private static GpRecord Iss() => OmmParser.Parse(File.ReadAllText(ApiHost.FixturePath))
        .Where(r => r.Elements.CatalogNumber == 25544).MaxBy(r => r.Elements.Epoch)!;

    private static async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static DateTimeOffset Time(JsonElement e) =>
        DateTimeOffset.Parse(e.GetProperty("timeUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A fake CelesTrak that answers the recorded response after a delay, and counts requests.</summary>
    private sealed class SlowCelesTrak(TimeSpan delay) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(ApiHost.FixturePath)) };
        }
    }
}
