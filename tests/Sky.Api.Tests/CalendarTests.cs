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
