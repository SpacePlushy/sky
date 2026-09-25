using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sky.Cli.Tests;

/// <summary>
/// End-to-end runs of the real commands, offline. The clock is set to the start of the Skyfield
/// reference window (2026-09-24 04:00 UTC), so printed values can be checked against Skyfield.
/// </summary>
public sealed partial class CommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
    private readonly CliHarness _cli = new(Now);

    public void Dispose() => _cli.Dispose();

    private static JsonElement Skyfield => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "iss-phoenix-2026-09-24.json"))).RootElement;

    [Fact]
    public async Task Passes_prints_the_next_5_iss_passes_in_phoenix_time()
    {
        int exit = await _cli.RunAsync("passes");

        Assert.Equal(0, exit);
        string output = _cli.Out.ToString();
        Assert.Contains("ISS (ZARYA)", output, StringComparison.Ordinal);
        Assert.Contains("America/Phoenix", output, StringComparison.Ordinal);

        var rises = PassRow().Matches(output).Select(m => DateTime.ParseExact(
            m.Groups["rise"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(5, rises.Count);

        // Printed times are Phoenix local. Rises fall on the 10 s grid from the clock time (whole
        // seconds), so each printed rise is 0 to 10 s after Skyfield's refined crossing.
        var reference = Skyfield.GetProperty("passes").EnumerateArray().Take(5).ToList();
        foreach (var (printedLocal, pass) in rises.Zip(reference))
        {
            var printedUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(printedLocal, Phoenix), TimeSpan.Zero);
            var skyfieldRise = pass.GetProperty("rise").GetProperty("utc").GetDateTimeOffset();
            Assert.InRange((printedUtc - skyfieldRise).TotalSeconds, -0.001, 10.001);
        }
    }

    [Fact]
    public async Task Now_prints_position_and_look_angles_that_match_skyfield()
    {
        int exit = await _cli.RunAsync("now");

        Assert.Equal(0, exit);
        string output = _cli.Out.ToString();
        var state = Skyfield.GetProperty("states")[0]; // 2026-09-24 04:00:00 UTC

        // The CLI runs the production path, which treats UTC as UT1 (assumption A5). Skyfield's
        // data used UT1 - UTC = dUT1 (+0.096 s here), so its Earth is rotated further by
        // delta = rate * dUT1. Rotation about the polar axis leaves latitude unchanged and shifts
        // longitude by exactly delta. For look angles and range the effect is bounded by the
        // satellite's displacement, delta times its distance from the axis, as in the cross-check.
        double dut1 = Number(state.GetProperty("ut1_minus_utc_s"));
        double delta = 7.2921158553e-5 * dut1;
        var earthFixed = state.GetProperty("earth_fixed_km");
        double shiftKm = delta * Math.Sqrt(Math.Pow(Number(earthFixed[0]), 2) + Math.Pow(Number(earthFixed[1]), 2));
        var look = state.GetProperty("look");
        double rangeKm = Number(look.GetProperty("range_km"));
        double angleBound = shiftKm / rangeKm * 180.0 / Math.PI;
        double horizontalKm = rangeKm * Math.Cos(Number(look.GetProperty("elevation_deg")) * Math.PI / 180.0);

        // Printed with 4 decimals (latitude, longitude) and 2 decimals (angles, range): half a unit
        // in the last place is added to each bound.
        Assert.Equal(Number(state.GetProperty("subpoint").GetProperty("latitude_converged_deg")), Printed(output, "latitude"), 5e-5);
        Assert.Equal(Number(state.GetProperty("subpoint").GetProperty("longitude_deg")) + (delta * 180.0 / Math.PI), Printed(output, "longitude"), 5e-5 + 1e-9);
        Assert.Equal(Number(look.GetProperty("azimuth_deg")), Printed(output, "azimuth"), 0.005 + (shiftKm / horizontalKm * 180.0 / Math.PI));
        Assert.Equal(Number(look.GetProperty("elevation_deg")), Printed(output, "elevation"), 0.005 + angleBound);
        Assert.Equal(rangeKm, Printed(output, "range"), 0.005 + shiftKm);
        Assert.Contains("2026-09-23 21:00:00", output, StringComparison.Ordinal); // 04:00 UTC in Phoenix
    }

    [Fact]
    public async Task Passes_states_the_largest_peak_uncertainty_among_the_listed_passes()
    {
        await _cli.RunAsync("passes");

        // The five listed passes carry uncertainties from 0.0178 to 0.0456 degrees; the largest,
        // from the 65.2 degree pass, prints rounded up to three decimals. CoarsePassFinderTests
        // verifies the uncertainty itself against exact geometry.
        Assert.Contains("rise and set within 10 s; peak within 0.1 s and 0.046°", _cli.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Printed_times_keep_their_stated_precision_from_a_fractional_clock_time()
    {
        // The search starts on the whole second at or before the clock, so rise and set print
        // exactly, and peaks print to tenths. The clock's fraction is chosen so the first pass
        // rises 0.2 s after a grid point: printing unrounded times would show that pass rising
        // before the true crossing, and rounding up instead would shift every rise by a second.
        _cli.Clock.SetUtcNow(Now.AddTicks(20_332_000)); // 04:00:02.332

        await _cli.RunAsync("passes");

        var reference = Skyfield.GetProperty("passes").EnumerateArray().Take(5).ToList();
        var rows = PassRow().Matches(_cli.Out.ToString()).ToList();
        Assert.Equal(5, rows.Count);
        foreach (var (row, pass) in rows.Zip(reference))
        {
            var riseUtc = ToUtc(DateTime.ParseExact(row.Groups["rise"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Assert.InRange((riseUtc - pass.GetProperty("rise").GetProperty("utc").GetDateTimeOffset()).TotalSeconds, -0.001, 10.001);
            Assert.Equal(0, (riseUtc - Now.AddSeconds(2)).Ticks % TimeSpan.FromSeconds(10).Ticks); // on the 04:00:02 grid

            var peakClock = TimeSpan.ParseExact(row.Groups["peak"].Value, @"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);
            var peakUtc = ToUtc(DateTime.ParseExact(row.Groups["rise"].Value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture) + peakClock);
            Assert.InRange((peakUtc - pass.GetProperty("culmination").GetProperty("utc").GetDateTimeOffset()).TotalSeconds, -0.101, 0.101);
        }
    }

    private static DateTimeOffset ToUtc(DateTime phoenixLocal) =>
        new(TimeZoneInfo.ConvertTimeToUtc(phoenixLocal, Phoenix), TimeSpan.Zero);

    [Fact]
    public async Task Now_during_a_pass_says_the_pass_is_in_progress()
    {
        // Set the clock to the peak of Skyfield's first pass.
        var peak = Skyfield.GetProperty("passes")[0].GetProperty("culmination").GetProperty("utc").GetDateTimeOffset();
        _cli.Clock.SetUtcNow(peak);

        int exit = await _cli.RunAsync("now");

        Assert.Equal(0, exit);
        Assert.Contains("in progress now", _cli.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Now_warns_when_the_satellites_elements_are_more_than_3_days_old()
    {
        _cli.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)); // ISS epoch is 2026-09-24 03:24

        int exit = await _cli.RunAsync("now");

        Assert.Equal(0, exit);
        Assert.Contains("ISS (ZARYA) elements are 4.4 days old", _cli.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Now_reports_a_pass_that_rises_within_the_current_second()
    {
        // Skyfield's first pass rises at 05:32:22.132. At 05:32:22.050 the ISS is still below 10
        // degrees, so this pass is the next one, rising on the 10 s grid from 05:32:22.
        _cli.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 24, 5, 32, 22, TimeSpan.Zero).AddMilliseconds(50));

        await _cli.RunAsync("now");

        Assert.Contains("next pass   rises 2026-09-23 22:32:32", _cli.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minimum_elevation_parses_the_same_in_every_locale()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            int exit = await _cli.RunAsync("passes", "--min-elevation", "30.5");

            Assert.Equal(0, exit);
            Assert.Contains("above 30.5°", _cli.Out.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("90")]
    [InlineData("-1")]
    [InlineData("1,5")]
    public async Task Invalid_minimum_elevations_are_rejected(string value)
    {
        int exit = await _cli.RunAsync("passes", "--min-elevation", value);

        Assert.NotEqual(0, exit);
        Assert.Empty(_cli.Requests);
    }

    [Fact]
    public async Task Rows_carry_utc_offsets_when_daylight_saving_changes_within_the_window()
    {
        // Denver falls back from UTC-6 to UTC-7 on 2026-11-01. Wall-clock times alone would be
        // ambiguous in the repeated hour, so each printed time carries its offset.
        _cli.WriteLocal(System.Text.Json.JsonSerializer.Serialize(new
        {
            Observer = new { TimeZone = "America/Denver" },
            CelesTrak = new { Groups = "stations", CacheDirectory = _cli.CacheDirectory },
        }));
        _cli.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

        int exit = await _cli.RunAsync("passes", "--days", "4", "--count", "20");

        Assert.Equal(0, exit);
        var rows = OffsetRow().Matches(_cli.Out.ToString()).ToList();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Groups["riseOffset"].Value == "-06:00");
        Assert.Contains(rows, r => r.Groups["riseOffset"].Value == "-07:00");
        foreach (var row in rows)
        {
            var rise = DateTimeOffset.Parse($"{row.Groups["date"].Value}T{row.Groups["rise"].Value}{row.Groups["riseOffset"].Value}", CultureInfo.InvariantCulture);
            var set = DateTimeOffset.Parse($"{row.Groups["date"].Value}T{row.Groups["set"].Value}{row.Groups["setOffset"].Value}", CultureInfo.InvariantCulture);
            Assert.True(set > rise, $"Row {row.Value.Trim()}: set is not after rise.");
        }
    }

    [Fact]
    public async Task Refresh_still_respects_the_2_hour_rule()
    {
        await _cli.RunAsync("passes");
        _cli.Clock.Advance(TimeSpan.FromHours(1));

        await _cli.RunAsync("passes", "--refresh");
        Assert.Single(_cli.Requests);
        Assert.Contains("2 hours", _cli.Error.ToString(), StringComparison.Ordinal);

        _cli.Clock.Advance(TimeSpan.FromHours(1));
        await _cli.RunAsync("passes", "--refresh");
        Assert.Equal(2, _cli.Requests.Count);
    }

    [Fact]
    public async Task Unblock_lets_a_blocked_group_be_requested_again()
    {
        await _cli.RunAsync("passes");
        _cli.NextAnswer = (System.Net.HttpStatusCode.Forbidden, "Forbidden for test");
        _cli.Clock.Advance(TimeSpan.FromHours(7));
        await _cli.RunAsync("passes");
        _cli.Clock.Advance(TimeSpan.FromHours(7));
        await _cli.RunAsync("passes");
        Assert.Equal(2, _cli.Requests.Count); // blocked: no third request

        int exit = await _cli.RunAsync("unblock", "stations");
        await _cli.RunAsync("passes");

        Assert.Equal(0, exit);
        Assert.Equal(3, _cli.Requests.Count);
    }

    [Fact]
    public async Task Unblock_with_an_invalid_group_name_fails_cleanly()
    {
        int exit = await _cli.RunAsync("unblock", "../secrets");

        Assert.Equal(1, exit);
        Assert.Contains("../secrets", _cli.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", _cli.Error.ToString(), StringComparison.Ordinal); // no stack trace
    }

    [Fact]
    public async Task Lookup_failure_names_groups_that_could_not_be_loaded()
    {
        _cli.WriteLocal(System.Text.Json.JsonSerializer.Serialize(new { CelesTrak = new { Groups = "stations,visual", CacheDirectory = _cli.CacheDirectory } }));
        _cli.UnreachableGroups.Add("visual");

        int exit = await _cli.RunAsync("passes", "--sat", "99999");

        Assert.Equal(1, exit);
        Assert.Contains("NORAD 99999 is not in stations", _cli.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("visual could not be loaded", _cli.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_run_within_6_hours_uses_the_cache_without_a_request()
    {
        await _cli.RunAsync("passes");
        _cli.Clock.Advance(TimeSpan.FromHours(1));

        int exit = await _cli.RunAsync("passes");

        Assert.Equal(0, exit);
        Assert.Single(_cli.Requests);
    }

    [Fact]
    public async Task Unknown_satellite_fails_with_a_clear_message()
    {
        int exit = await _cli.RunAsync("passes", "--sat", "99999");

        Assert.Equal(1, exit);
        Assert.Contains("99999", _cli.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_celestrak_with_no_cache_fails_and_explains()
    {
        _cli.CelesTrakReachable = false;

        int exit = await _cli.RunAsync("now");

        Assert.Equal(1, exit);
        Assert.Contains("Could not reach CelesTrak", _cli.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_settings_fail_before_any_request()
    {
        _cli.WriteLocal("""{"Observer":{"LatitudeDegrees":123}}""");

        int exit = await _cli.RunAsync("passes");

        Assert.Equal(1, exit);
        Assert.Contains("Observer:LatitudeDegrees", _cli.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_cli.Requests);
    }

    private static double Number(JsonElement e) => e.GetDouble();

    /// <summary>Reads a labeled number such as "azimuth 99.08" from the output.</summary>
    private static double Printed(string output, string label)
    {
        var match = Regex.Match(output, $@"{label}\s+(-?[0-9]+\.[0-9]+)", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"No \"{label}\" value in output:\n{output}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\s*(?<rise>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s+\S+\s+(?<peak>\d{2}:\d{2}:\d{2}\.\d)\s", RegexOptions.Multiline)]
    private static partial Regex PassRow();

    [GeneratedRegex(@"^\s*(?<date>\d{4}-\d{2}-\d{2}) (?<rise>\d{2}:\d{2}:\d{2})(?<riseOffset>[+-]\d{2}:\d{2})\s+\S+\s+\S+\s+\S+\s+\S+\s+(?<set>\d{2}:\d{2}:\d{2})(?<setOffset>[+-]\d{2}:\d{2})", RegexOptions.Multiline)]
    private static partial Regex OffsetRow();
}
