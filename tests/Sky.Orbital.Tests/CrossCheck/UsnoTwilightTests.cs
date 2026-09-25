using System.Globalization;
using System.Text.Json;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;

namespace Sky.Orbital.Tests.CrossCheck;

/// <summary>
/// Civil twilight at the default observer against the U.S. Naval Observatory's published times
/// (Data/Usno/README.md), a source independent of both Sky and Skyfield.
/// </summary>
/// <remarks>
/// USNO prints times rounded to the minute, so its value is within 30 s of its own crossing. Sky's
/// crossing is within 0.0115° of Sun direction over the Sun's elevation rate at that moment, plus
/// 1 ms of root-finding. USNO's model (full ephemeris, its own UT1 and ΔT) can differ from the true
/// crossing by well under 1 s. Each crossing's bound is the sum.
/// </remarks>
public class UsnoTwilightTests
{
    private static readonly TopocentricFrame Phoenix = new(new Geodetic(33.4478, -112.0972, 0.331));

    [Theory]
    [InlineData("2026-03-20")]
    [InlineData("2026-06-21")]
    [InlineData("2026-09-24")]
    [InlineData("2026-12-21")]
    public void Civil_twilight_matches_usno_to_its_printed_minute(string date)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Usno", $"phoenix-{date}.json")));
        var sun = json.RootElement.GetProperty("properties").GetProperty("data").GetProperty("sundata").EnumerateArray()
            .ToDictionary(e => e.GetProperty("phen").GetString()!, e => e.GetProperty("time").GetString()!);

        // The local day in UTC-7 (the request's tz), 07:00 UTC to 07:00 UTC the next day.
        var dayStart = DateTimeOffset.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).AddHours(7);
        var crossings = Visibility.SignChanges(s => Visibility.SunElevationDegrees(Phoenix, dayStart.AddSeconds(s)) + 6.0, _ => 0.0043, 86400.0)
            .Select(s => dayStart.AddTicks((long)Math.Round(s * TimeSpan.TicksPerSecond)))
            .ToList();
        Assert.Equal(2, crossings.Count);

        Check(crossings[0], sun["Begin Civil Twilight"]);
        Check(crossings[1], sun["End Civil Twilight"]);

        void Check(DateTimeOffset sky, string usnoClock)
        {
            var usno = dayStart.Add(TimeSpan.ParseExact(usnoClock, @"hh\:mm", CultureInfo.InvariantCulture));
            double rate = Math.Abs(Visibility.SunElevationDegrees(Phoenix, sky.AddSeconds(30)) - Visibility.SunElevationDegrees(Phoenix, sky.AddSeconds(-30))) / 60.0;
            double bound = 30.0 + (0.0115 / rate) + 0.001 + 1.0;
            double difference = (sky - usno).TotalSeconds;
            Assert.True(Math.Abs(difference) <= bound, $"{date}: Sky {sky:HH:mm:ss} UTC, USNO {usnoClock} local: {difference} s against {bound} s.");
        }
    }
}
