using System.Net;
using System.Text;
using Sky.CelesTrak;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Api.Tests;

/// <summary>The iCalendar export against RFC 5545's rules and against the pass finder's own visible parts.</summary>
public sealed class CalendarTests : IDisposable
{
    private readonly ApiHost _host = new();
    private readonly HttpClient _client;

    public CalendarTests() => _client = _host.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Fact]
    public void Text_is_escaped_as_rfc_5545_requires()
    {
        Assert.Equal(@"a\,b\;c\\d\ne\nf", Calendar.Escape("a,b;c\\d\ne\r\nf"));
    }

    [Theory]
    [InlineData("2026-09-24T20:07:26.642Z", "20260924T200727Z")]
    [InlineData("2026-09-24T20:07:26.499Z", "20260924T200726Z")]
    [InlineData("2026-09-24T23:59:59.500Z", "20260925T000000Z")]
    public void Times_are_utc_rounded_to_the_nearest_second(string instant, string expected)
    {
        Assert.Equal(expected, Calendar.Utc(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Long_lines_fold_at_75_octets_without_splitting_characters()
    {
        // Degree signs are two octets in UTF-8, so a fold counted in characters would overrun.
        string summary = string.Concat(Enumerable.Repeat("ISS 36° NNE, ", 20));
        string file = Calendar.Write("Test", [new CalendarEvent(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), summary, "x", "k")], DateTimeOffset.UnixEpoch, 10);

        Assert.DoesNotContain("\n", file.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        foreach (string physical in file.Split("\r\n").Where(l => l.Length > 0))
        {
            Assert.True(Encoding.UTF8.GetByteCount(physical) <= 75, $"{Encoding.UTF8.GetByteCount(physical)} octets: {physical}");
        }

        // Unfolding (removing CRLF followed by a space) gives back the escaped content line.
        var unfolded = Unfold(file);
        Assert.Contains("SUMMARY:" + Calendar.Escape(summary), unfolded);
    }

    [Fact]
    public async Task The_export_has_one_event_per_visible_part_with_an_alarm()
    {
        using var response = await _client.GetAsync(new Uri("/api/satellites/25544/passes.ics", UriKind.Relative), TestContext.Current.CancellationToken);
        string file = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("sky-25544-passes.ics", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var lines = Unfold(file);
        Assert.Equal("BEGIN:VCALENDAR", lines[0]);
        Assert.Equal("END:VCALENDAR", lines[^1]);
        Assert.Contains("VERSION:2.0", lines);

        // The same visible parts the core finds, in order, each starting on its rounded time.
        var expected = VisibleParts(7);
        var starts = lines.Where(l => l.StartsWith("DTSTART:", StringComparison.Ordinal)).Select(l => l["DTSTART:".Length..]).ToList();
        var ends = lines.Where(l => l.StartsWith("DTEND:", StringComparison.Ordinal)).Select(l => l["DTEND:".Length..]).ToList();
        Assert.Equal(expected.Select(w => Calendar.Utc(w.Start.Time)), starts);
        Assert.Equal(expected.Select(w => Calendar.Utc(w.End.Time)), ends);
        Assert.NotEmpty(expected);

        // Every event is complete, with a unique UID and an alarm 10 minutes before.
        var uids = lines.Where(l => l.StartsWith("UID:", StringComparison.Ordinal)).ToList();
        Assert.Equal(expected.Count, uids.Count);
        Assert.Equal(uids.Count, uids.Distinct().Count());
        Assert.Equal(expected.Count, lines.Count(l => l == "TRIGGER:-PT10M"));
        Assert.Equal(lines.Count(l => l == "BEGIN:VEVENT"), lines.Count(l => l == "END:VEVENT"));
        Assert.All(lines.Where(l => l.StartsWith("SUMMARY:", StringComparison.Ordinal)), l => Assert.Contains(@"visible\, up to", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_passes_and_a_custom_alarm_can_be_exported()
    {
        string file = await _client.GetStringAsync(new Uri("/api/satellites/25544/passes.ics?visibleOnly=false&alarm=0&days=2", UriKind.Relative), TestContext.Current.CancellationToken);
        var lines = Unfold(file);

        var propagator = Sgp4Propagator.Create(Iss().Elements);
        int passes = PassFinder.Find(propagator, Phoenix, ApiHost.Start, ApiHost.Start.AddDays(2), 10.0).Passes.Count;
        Assert.Equal(passes, lines.Count(l => l == "BEGIN:VEVENT"));
        Assert.Equal(passes, lines.Count(l => l == "TRIGGER:-PT0M"));
    }

    [Fact]
    public async Task Uids_are_stable_between_exports()
    {
        string first = await _client.GetStringAsync(new Uri("/api/satellites/25544/passes.ics", UriKind.Relative), TestContext.Current.CancellationToken);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        string second = await _client.GetStringAsync(new Uri("/api/satellites/25544/passes.ics", UriKind.Relative), TestContext.Current.CancellationToken);

        static IEnumerable<string> Uids(string file) => Unfold(file).Where(l => l.StartsWith("UID:", StringComparison.Ordinal));
        Assert.Equal(Uids(first), Uids(second));
    }

    [Theory]
    [InlineData(0.01)]   // 10 ms of along-track shift: enough to move a rise across a minute boundary
    [InlineData(-0.01)]
    [InlineData(30.0)]   // half a minute
    [InlineData(-30.0)]
    public async Task Uids_survive_new_elements_that_move_the_passes(double seconds)
    {
        // A later element set moves each pass by seconds. The UIDs must not change, or importing the
        // new file adds a second copy of every event, alarm included. The demo week has a pass rising
        // at 03:56:00.006 UTC, so a key made from the rise minute would change at a 10 ms shift. The
        // shifted set stays self-consistent: the recorded epoch is 0.1° past the ascending node, so a
        // shift that moves the satellite back across the node lowers REV_AT_EPOCH by one, as a real
        // element set's would.
        var iss = Iss().Elements;
        double degrees = iss.MeanMotion * 360.0 / 86400.0 * seconds; // mean anomaly change for that shift
        double u0 = (iss.ArgumentOfPericenter + iss.MeanAnomaly) % 360.0;
        long revolutionChange = u0 + degrees < 0 ? -1 : u0 + degrees >= 360 ? 1 : 0;
        using var moved = new ApiHost(seed: false, seedFiles: folder =>
        {
            var records = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ApiHost.FixturePath))!.AsArray();
            var record = records.First(r => r!["NORAD_CAT_ID"]!.GetValue<long>() == 25544)!;
            record["MEAN_ANOMALY"] = record["MEAN_ANOMALY"]!.GetValue<double>() + degrees;
            record["REV_AT_EPOCH"] = record["REV_AT_EPOCH"]!.GetValue<long>() + revolutionChange;
            File.WriteAllText(Path.Combine(folder, "stations.json"), records.ToJsonString());
        });
        using var movedClient = moved.CreateClient();

        // The shift really moved the passes: by the shift, within 20% (the satellite's rate along
        // its orbit differs from the mean motion by a few percent), and the opposite way, since a
        // satellite ahead in its orbit arrives earlier. Peak times carry milliseconds.
        var before = await PeakTimes(_client);
        var after = await PeakTimes(movedClient);
        Assert.Equal(before.Count, after.Count);
        double low = Math.Min(-0.8 * seconds, -1.2 * seconds) - 0.002;
        double high = Math.Max(-0.8 * seconds, -1.2 * seconds) + 0.002;
        Assert.All(before.Zip(after), pair => Assert.InRange((pair.Second - pair.First).TotalSeconds, low, high));
        Assert.Contains(before.Zip(after), pair => pair.First != pair.Second);

        string original = await _client.GetStringAsync(new Uri("/api/satellites/25544/passes.ics?visibleOnly=false", UriKind.Relative), TestContext.Current.CancellationToken);
        string shiftedFile = await movedClient.GetStringAsync(new Uri("/api/satellites/25544/passes.ics?visibleOnly=false", UriKind.Relative), TestContext.Current.CancellationToken);

        static List<string> Uids(string file) => Unfold(file).Where(l => l.StartsWith("UID:", StringComparison.Ordinal)).ToList();
        Assert.Equal(Uids(original), Uids(shiftedFile));

        static async Task<List<DateTimeOffset>> PeakTimes(HttpClient client)
        {
            string json = await client.GetStringAsync(new Uri("/api/satellites/25544/passes", UriKind.Relative), TestContext.Current.CancellationToken);
            return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("passes").EnumerateArray()
                .Select(p => DateTimeOffset.Parse(p.GetProperty("culmination").GetProperty("timeUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
        }
    }

    [Fact]
    public void Pass_keys_count_revolutions_one_per_orbit()
    {
        // Consecutive passes on consecutive orbits (about 93 minutes apart) differ by exactly one
        // revolution; passes on the same revolution cannot both occur. The keys come from the
        // recorded REV_AT_EPOCH, 58709.
        var record = Iss();
        Assert.Equal(58709, record.RevolutionAtEpoch);
        var propagator = Sgp4Propagator.Create(record.Elements);
        var passes = PassFinder.Find(propagator, Phoenix, ApiHost.Start, ApiHost.Start.AddDays(7), 10.0).Passes;
        var revolutions = passes.Select(p => long.Parse(SatelliteService.PassKey(record, p, propagator).Split("-r")[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(revolutions.Count, revolutions.Distinct().Count());
        for (int k = 1; k < passes.Count; k++)
        {
            double orbits = (passes[k].Culmination.Time - passes[k - 1].Culmination.Time).TotalDays * record.Elements.MeanMotion;
            Assert.Equal(Math.Round(orbits), revolutions[k] - revolutions[k - 1]);
        }

        // The first pass is within a day of the epoch: about 15.5 revolutions a day.
        Assert.InRange(revolutions[0], 58709, 58709 + 16);
    }

    [Fact]
    public async Task Every_event_has_a_stamp_an_alarm_description_and_times_that_match_its_own()
    {
        string file = await _client.GetStringAsync(new Uri("/api/satellites/25544/passes.ics", UriKind.Relative), TestContext.Current.CancellationToken);
        var events = SplitEvents(Unfold(file));
        Assert.NotEmpty(events);
        var phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        foreach (var e in events)
        {
            Assert.Single(e, l => l.StartsWith("DTSTAMP:", StringComparison.Ordinal) && l.EndsWith('Z'));
            int alarm = e.IndexOf("BEGIN:VALARM");
            Assert.True(alarm > 0);
            Assert.Contains(e.Skip(alarm), l => l.StartsWith("DESCRIPTION:", StringComparison.Ordinal) && l.Length > "DESCRIPTION:".Length);

            // The description's "Visible from ... to ..." times are DTSTART and DTEND, in Phoenix time.
            string description = e.First(l => l.StartsWith("DESCRIPTION:", StringComparison.Ordinal));
            DateTime Start(string prefix) => DateTime.ParseExact(e.First(l => l.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..], "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, phoenix).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains($"Visible from {Local(Start("DTSTART:"))} ", description, StringComparison.Ordinal);
            Assert.Contains($" to {Local(Start("DTEND:"))} ", description, StringComparison.Ordinal);
            Assert.Contains("Predicted from elements with epoch", description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_satellite_with_nothing_visible_gets_a_problem_not_an_empty_calendar()
    {
        // RFC 5545 requires at least one component. Find a satellite in the recorded group with
        // passes but no visible part in the next day, by the core itself.
        var all = OmmParser.Parse(File.ReadAllText(ApiHost.FixturePath));
        long? dark = null;
        foreach (var record in all.Where(r => r.EphemerisType == 0))
        {
            var propagator = Sgp4Propagator.Create(record.Elements);
            var passes = PassFinder.Find(propagator, Phoenix, ApiHost.Start, ApiHost.Start.AddDays(1), 10.0).Passes;
            if (passes.Count > 0 && passes.All(p => Visibility.Windows(p, propagator, Phoenix).Count == 0))
            {
                dark = record.Elements.CatalogNumber;
                break;
            }
        }

        Assert.True(dark.HasValue, "No satellite in the fixture lacks a visible pass in the next day.");
        using var response = await _client.GetAsync(new Uri($"/api/satellites/{dark}/passes.ics?days=1", UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("No passes to export", body, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Calendar.Write("x", [], DateTimeOffset.UnixEpoch, 10));
    }

    private static List<List<string>> SplitEvents(List<string> lines)
    {
        var events = new List<List<string>>();
        List<string>? current = null;
        foreach (string line in lines)
        {
            if (line == "BEGIN:VEVENT")
            {
                current = [];
            }
            else if (line == "END:VEVENT" && current is not null)
            {
                events.Add(current);
                current = null;
            }
            else
            {
                current?.Add(line);
            }
        }

        return events;
    }

    [Theory]
    [InlineData("/api/satellites/25544/passes.ics?days=0", "Invalid number of days")]
    [InlineData("/api/satellites/25544/passes.ics?alarm=121", "Invalid alarm")]
    public async Task Bad_queries_are_problem_details(string url, string title)
    {
        using var response = await _client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(title, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, "N")]
    [InlineData(11.24, "N")]
    [InlineData(11.26, "NNE")]
    [InlineData(90.0, "E")]
    [InlineData(341.1, "NNW")]
    [InlineData(348.76, "N")]
    [InlineData(359.99, "N")]
    public void Compass_points_split_the_circle_into_16(double azimuth, string expected)
    {
        Assert.Equal(expected, SatelliteService.Compass(azimuth));
    }

    private static readonly TopocentricFrame Phoenix = new(new Geodetic(33.4478, -112.0972, 0.331));

    private static GpRecord Iss() => OmmParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations-2026-09-24.json")))
        .Where(r => r.Elements.CatalogNumber == 25544).MaxBy(r => r.Elements.Epoch)!;

    private static List<VisibleWindow> VisibleParts(int days)
    {
        var propagator = Sgp4Propagator.Create(Iss().Elements);
        return PassFinder.Find(propagator, Phoenix, ApiHost.Start, ApiHost.Start.AddDays(days), 10.0).Passes
            .SelectMany(p => Visibility.Windows(p, propagator, Phoenix))
            .ToList();
    }

    /// <summary>Unfolds an iCalendar file into its content lines.</summary>
    private static List<string> Unfold(string file) =>
        file.Replace("\r\n ", string.Empty, StringComparison.Ordinal).Split("\r\n").Where(l => l.Length > 0).ToList();
}
