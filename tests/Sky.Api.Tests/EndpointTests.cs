using System.Net;
using System.Text.Json;
using Sky.CelesTrak;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Api.Tests;

/// <summary>
/// Every endpoint against the orbital core it wraps, and against the same Skyfield reference the
/// CLI is checked with. The API must not compute anything differently from the core.
/// </summary>
public sealed class EndpointTests : IDisposable
{
    private static readonly JsonElement Skyfield = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "iss-phoenix-2026-09-24.json"))).RootElement;

    private static readonly GpRecord IssRecord = OmmParser.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations-2026-09-24.json")))
        .Where(r => r.Elements.CatalogNumber == 25544).MaxBy(r => r.Elements.Epoch)!;

    private static readonly TopocentricFrame Phoenix = new(new Geodetic(33.4478, -112.0972, 0.331));

    private readonly ApiHost _host = new();
    private readonly HttpClient _client;

    public EndpointTests() => _client = _host.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Config_gives_the_observer_zone_and_server_time()
    {
        var json = await GetJson("/api/config");

        Assert.Equal("Arizona State Capitol, Phoenix", json.GetProperty("observer").GetProperty("name").GetString());
        Assert.Equal("America/Phoenix", json.GetProperty("observer").GetProperty("timeZone").GetString());
        Assert.Equal(331.0, json.GetProperty("observer").GetProperty("heightM").GetDouble(), 1e-9);
        Assert.Equal(10.0, json.GetProperty("minimumElevationDeg").GetDouble());
        Assert.Equal(25544, json.GetProperty("satellites")[0].GetInt64());
        Assert.True(json.GetProperty("offline").GetBoolean());
        Assert.False(json.GetProperty("clockSimulated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("clockStartUtc").ValueKind);
        Assert.Equal("2026-09-24T04:00:00Z", json.GetProperty("serverTimeUtc").GetString());
    }

    [Fact]
    public async Task Now_matches_skyfield_as_the_cli_does()
    {
        var json = await GetJson("/api/satellites/25544/now");
        var state = Skyfield.GetProperty("states")[0]; // 2026-09-24 04:00:00 UTC

        // As in the CLI test: the API treats UTC as UT1 (assumption A5) and Skyfield's data used
        // UT1 − UTC = dUT1, so Skyfield's Earth is rotated further by delta = rate × dUT1. Rotation
        // about the pole leaves latitude alone, shifts longitude by delta, and moves look angles by
        // at most the satellite's displacement over the range.
        double dut1 = state.GetProperty("ut1_minus_utc_s").GetDouble();
        double delta = 7.2921158553e-5 * dut1;
        var earthFixed = state.GetProperty("earth_fixed_km");
        double shiftKm = delta * Math.Sqrt(Math.Pow(earthFixed[0].GetDouble(), 2) + Math.Pow(earthFixed[1].GetDouble(), 2));
        var look = state.GetProperty("look");
        double rangeKm = look.GetProperty("range_km").GetDouble();
        double angleBound = (shiftKm / rangeKm * 180.0 / Math.PI) + 1e-9;

        Assert.Equal("2026-09-24T04:00:00Z", json.GetProperty("timeUtc").GetString());
        var position = json.GetProperty("position");
        Assert.Equal(state.GetProperty("subpoint").GetProperty("latitude_converged_deg").GetDouble(), position.GetProperty("latitudeDeg").GetDouble(), 1e-8);
        Assert.Equal(state.GetProperty("subpoint").GetProperty("longitude_deg").GetDouble() + (delta * 180.0 / Math.PI), position.GetProperty("longitudeDeg").GetDouble(), 1e-8);
        Assert.Equal(look.GetProperty("elevation_deg").GetDouble(), json.GetProperty("look").GetProperty("elevationDeg").GetDouble(), angleBound);
        Assert.Equal(rangeKm, json.GetProperty("look").GetProperty("rangeKm").GetDouble(), shiftKm + 1e-6);
        Assert.Equal("ISS (ZARYA)", json.GetProperty("satellite").GetProperty("name").GetString());
        Assert.Equal(1440.0 / IssRecord.Elements.MeanMotion, json.GetProperty("satellite").GetProperty("periodMinutes").GetDouble(), 1e-12);

        // The rest is the orbital core's own output, so it must match exactly.
        var ecef = EarthRotation.TemeToEcef(Sgp4Propagator.Create(IssRecord.Elements).Propagate(ApiHost.Start).State, ApiHost.Start);
        Assert.Equal(Visibility.IsSunlit(Sgp4Propagator.Create(IssRecord.Elements), ApiHost.Start), json.GetProperty("sunlit").GetBoolean());
        Assert.Equal(Visibility.SunElevationDegrees(Phoenix, ApiHost.Start), json.GetProperty("sun").GetProperty("elevationDeg").GetDouble(), 1e-12);
        Assert.Equal(Phoenix.LookAt(ecef).AzimuthDegrees, json.GetProperty("look").GetProperty("azimuthDeg").GetDouble(), 1e-12);
    }

    [Fact]
    public async Task Now_during_a_pass_reports_it_with_its_real_rise_and_set()
    {
        var first = Skyfield.GetProperty("passes")[0];
        string peak = first.GetProperty("culmination").GetProperty("utc").GetString()!;

        var json = await GetJson($"/api/satellites/25544/now?at={Uri.EscapeDataString(peak)}");

        var pass = json.GetProperty("currentPass");
        Assert.Equal(JsonValueKind.Object, pass.ValueKind);
        Assert.InRange((Time(pass.GetProperty("rise")) - first.GetProperty("rise").GetProperty("utc").GetDateTimeOffset()).TotalSeconds, -0.0011, 0.0011);
        Assert.InRange((Time(pass.GetProperty("set")) - first.GetProperty("set").GetProperty("utc").GetDateTimeOffset()).TotalSeconds, -0.0011, 0.0011);
    }

    [Fact]
    public async Task Passes_are_the_pass_finders_and_visibility_output()
    {
        var json = await GetJson("/api/satellites/25544/passes?days=7");

        var propagator = Sgp4Propagator.Create(IssRecord.Elements);
        var expected = PassFinder.Find(propagator, Phoenix, ApiHost.Start, ApiHost.Start.AddDays(7), 10.0).Passes;
        var passes = json.GetProperty("passes").EnumerateArray().ToList();
        Assert.Equal(expected.Count, passes.Count);
        foreach (var (dto, pass) in passes.Zip(expected))
        {
            Assert.Equal(Ms(pass.Rise.Time), Time(dto.GetProperty("rise")));
            Assert.Equal(Ms(pass.Culmination.Time), Time(dto.GetProperty("culmination")));
            Assert.Equal(Ms(pass.Set.Time), Time(dto.GetProperty("set")));
            Assert.Equal(pass.Culmination.ElevationDegrees, dto.GetProperty("culmination").GetProperty("elevationDeg").GetDouble());

            var windows = Visibility.Windows(pass, propagator, Phoenix);
            var visible = dto.GetProperty("visible").EnumerateArray().ToList();
            Assert.Equal(windows.Count, visible.Count);
            var path = dto.GetProperty("path").EnumerateArray().ToList();
            var pathTimes = path.Select(Time).ToList();
            foreach (var (v, w) in visible.Zip(windows))
            {
                Assert.Equal(Ms(w.Start.Time), Time(v.GetProperty("start")));
                Assert.Equal(Ms(w.End.Time), Time(v.GetProperty("end")));

                // The path carries each visible part's ends exactly, for the sky plot.
                Assert.Contains(Ms(w.Start.Time), pathTimes);
                Assert.Contains(Ms(w.End.Time), pathTimes);
            }

            // The path runs from rise to set, in order, never more than 10 s apart, and every
            // 10 s step from rise is present.
            Assert.Equal(Ms(pass.Rise.Time), pathTimes[0]);
            Assert.Equal(Ms(pass.Set.Time), pathTimes[^1]);
            for (int k = 1; k < pathTimes.Count; k++)
            {
                Assert.InRange((pathTimes[k] - pathTimes[k - 1]).TotalSeconds, 0.0005, 10.0005);
            }

            for (var t = pass.Rise.Time; t < pass.Set.Time; t = t.AddSeconds(10))
            {
                Assert.Contains(Ms(t), pathTimes);
            }
        }

        // The first pass is up at 04:00 (it rose earlier); the next is Skyfield's first.
        var skyfieldFirst = Skyfield.GetProperty("passes")[0];
        Assert.True(Time(passes[0].GetProperty("rise")) < ApiHost.Start);
        Assert.InRange((Time(passes[1].GetProperty("rise")) - skyfieldFirst.GetProperty("rise").GetProperty("utc").GetDateTimeOffset()).TotalSeconds, -0.0011, 0.0011);
        Assert.Contains(passes, p => p.GetProperty("visible").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Track_spans_one_orbit_either_side_every_30_seconds_on_the_cores_subpoints()
    {
        var json = await GetJson("/api/satellites/25544/track");

        double period = 1440.0 / IssRecord.Elements.MeanMotion;
        var points = json.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(30.0, json.GetProperty("stepSeconds").GetDouble());
        Assert.Equal((int)Math.Floor(2 * period * 60 / 30) + 1, points.Count);
        Assert.Equal(ApiHost.Start.AddMinutes(-period), Time(points[0]), TimeSpan.FromMilliseconds(1));

        // Every point's values belong to the whole-millisecond time it reports.

        var propagator = Sgp4Propagator.Create(IssRecord.Elements);
        foreach (var point in points.Where((_, i) => i % 37 == 0))
        {
            var t = Time(point);
            var subpoint = Wgs84.FromEcef(EarthRotation.TemeToEcef(propagator.Propagate(t).State, t).Position);
            Assert.Equal(subpoint.LatitudeDegrees, point.GetProperty("latitudeDeg").GetDouble(), 1e-12);
            Assert.Equal(subpoint.LongitudeDegrees, point.GetProperty("longitudeDeg").GetDouble(), 1e-12);
        }
    }

    [Fact]
    public async Task The_subsolar_point_matches_de421()
    {
        // The Sun's direction is checked against DE421 to 0.0115° in the orbital tests; the
        // subsolar point is that direction's latitude and longitude, so the same bound applies.
        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "iss-phoenix-visibility-2026-09-24.json")))
            .RootElement.GetProperty("sun_week")[37];
        string at = sample.GetProperty("utc").GetString()!;
        var sun = sample.GetProperty("earth_fixed_km");
        double x = sun[0].GetDouble(), y = sun[1].GetDouble(), z = sun[2].GetDouble();

        var json = await GetJson($"/api/satellites/25544/now?at={Uri.EscapeDataString(at)}");

        Assert.Equal(Math.Atan2(z, Math.Sqrt((x * x) + (y * y))) * 180 / Math.PI, json.GetProperty("sun").GetProperty("subsolarLatitudeDeg").GetDouble(), 0.0115);
        Assert.Equal(Math.Atan2(y, x) * 180 / Math.PI, json.GetProperty("sun").GetProperty("subsolarLongitudeDeg").GetDouble(), 0.0115 / Math.Cos(Math.Atan2(z, Math.Sqrt((x * x) + (y * y)))));
    }

    [Theory]
    [InlineData(420.0, 0.0)]
    [InlineData(420.0, 10.0)]
    [InlineData(35_786.0, 10.0)]
    [InlineData(800.0, 45.0)]
    public void The_footprint_radius_puts_the_satellite_at_the_given_elevation(double heightKm, double elevationDeg)
    {
        // On the sphere of mean radius: an observer at central angle λ from the subpoint sees the
        // satellite at elevation e with tan e = (cos λ − R/(R+h)) / sin λ. Invert-and-check.
        const double R = 6371.0088;
        double lambda = SatelliteService.FootprintRadiusDegrees(heightKm, elevationDeg) * Math.PI / 180;
        double e = Math.Atan2(Math.Cos(lambda) - (R / (R + heightKm)), Math.Sin(lambda)) * 180 / Math.PI;
        Assert.Equal(elevationDeg, e, 1e-9);
    }

    [Fact]
    public async Task Satellites_lists_the_cached_group_with_the_featured_one_first()
    {
        var list = (await GetJson("/api/satellites")).EnumerateArray().ToList();

        Assert.Equal(22, list.Count);
        Assert.Equal(25544, list[0].GetProperty("id").GetInt64());
        Assert.True(list[0].GetProperty("featured").GetBoolean());
        Assert.Equal("stations", list[0].GetProperty("group").GetString());
    }

    [Theory]
    [InlineData("/api/satellites/99999/now", 404, "Unknown satellite")]
    [InlineData("/api/satellites/25544/passes?days=0", 400, "Invalid number of days")]
    [InlineData("/api/satellites/25544/passes?days=11", 400, "Invalid number of days")]
    [InlineData("/api/satellites/25544/passes?minElevation=90", 400, "Invalid minimum elevation")]
    [InlineData("/api/satellites/25544/track?minutes=0", 400, "Invalid track length")]
    [InlineData("/api/nothing-here", 404, "Not found")]
    [InlineData("/api/satellites/abc/now", 404, "Not found")]
    public async Task Errors_are_problem_details_without_internals(string url, int status, string title)
    {
        using var response = await _client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(title, JsonDocument.Parse(body).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_cached_the_api_answers_503_and_says_why()
    {
        using var empty = new ApiHost(seed: false);
        using var client = empty.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/satellites/25544/now", UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Offline mode", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_reports_the_cached_group()
    {
        var json = await GetJson("/api/health");

        Assert.Equal("ok", json.GetProperty("status").GetString());
        var group = json.GetProperty("groups")[0];
        Assert.Equal("stations", group.GetProperty("group").GetString());
        Assert.Equal(22, group.GetProperty("records").GetInt32());
        Assert.Equal("Cache", group.GetProperty("source").GetString());
    }

    private async Task<JsonElement> GetJson(string url)
    {
        using var response = await _client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>An instant truncated to the millisecond, as the API sends it.</summary>
    private static DateTimeOffset Ms(DateTimeOffset t) => t.AddTicks(-(t.UtcTicks % TimeSpan.TicksPerMillisecond));

    private static DateTimeOffset Time(JsonElement e)
    {
        string text = e.GetProperty("timeUtc").GetString()!;
        Assert.EndsWith("Z", text, StringComparison.Ordinal);
        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }
}
