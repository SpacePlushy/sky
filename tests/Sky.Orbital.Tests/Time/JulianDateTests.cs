using Sky.Orbital.Time;

namespace Sky.Orbital.Tests.Time;

public class JulianDateTests
{
    [Fact]
    public void J2000_epoch_is_julian_date_2451545()
    {
        // J2000.0 is defined as 2000 January 1, 12:00, Julian date 2451545.0.
        var jd = JulianDate.FromInstant(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(2451545.0, jd.Value);
        Assert.Equal(0.0, jd.DaysSinceJ2000);
    }

    [Fact]
    public void Splits_at_the_preceding_midnight()
    {
        // 1992 August 20 12:14 (Vallado, Fundamentals, Example 3-5): midnight is JD 2448854.5,
        // and 12:14 is 734 minutes, or 0.50972222... of a day.
        var jd = JulianDate.FromInstant(new DateTimeOffset(1992, 8, 20, 12, 14, 0, TimeSpan.Zero));

        Assert.Equal(2448854.5, jd.Whole);
        Assert.Equal(734.0 / 1440.0, jd.Fraction, 1e-15);
        Assert.Equal(2448855.009722222, jd.Value, 1e-9);
    }

    [Fact]
    public void Keeps_sub_microsecond_resolution_that_a_single_double_would_lose()
    {
        // A single double near JD 2.46e6 resolves only about 40 microseconds, so it would read this
        // 1 microsecond step as 0 or 40. Days since J2000 (about 1e4) resolves about 0.16 microseconds.
        var t0 = new DateTimeOffset(2026, 9, 24, 3, 24, 21, TimeSpan.Zero);
        var t1 = t0.AddTicks(10); // 1 microsecond later

        double difference = JulianDate.FromInstant(t1).DaysSinceJ2000 - JulianDate.FromInstant(t0).DaysSinceJ2000;

        Assert.Equal(1e-6 / 86400.0, difference, 2e-12);
    }

    [Fact]
    public void Uses_the_instant_not_the_local_clock_time()
    {
        var utc = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var phoenix = utc.ToOffset(TimeSpan.FromHours(-7)); // same instant, previous calendar day in Phoenix

        Assert.Equal(JulianDate.FromInstant(utc), JulianDate.FromInstant(phoenix));
    }
}
