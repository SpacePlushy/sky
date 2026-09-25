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
    public void Split_is_exact_across_1900_to_2100_including_either_side_of_midnight()
    {
        // Whole is the Julian date of the preceding midnight (ends in .5), Fraction is in [0, 1), and
        // together they are the instant: whole days exactly, and the day's ticks to rounding.
        var random = new Random(1900);
        var epoch1900 = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var instants = new List<DateTimeOffset>();
        for (int i = 0; i < 3000; i++)
        {
            var day = epoch1900.AddDays(random.Next(0, 73049)); // through 2099-12-31
            instants.Add(day.AddTicks((long)(random.NextDouble() * TimeSpan.TicksPerDay)));
            instants.Add(day.AddTicks(-1));
            instants.Add(day);
        }

        var j2000Midnight = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var instant in instants)
        {
            var jd = JulianDate.FromInstant(instant);

            Assert.Equal(0.0, (jd.Whole - 0.5) % 1.0);
            Assert.InRange(jd.Fraction, 0.0, 1.0 - 1e-17);
            long wholeDaysTicks = (long)(jd.Whole - 2451544.5) * TimeSpan.TicksPerDay;
            long expectedTicks = (instant - j2000Midnight).Ticks;
            Assert.Equal(expectedTicks - wholeDaysTicks, jd.Fraction * TimeSpan.TicksPerDay, 1e-3);
        }
    }

    [Fact]
    public void Days_since_j2000_is_continuous_across_midnight()
    {
        var random = new Random(2100);
        for (int i = 0; i < 1000; i++)
        {
            var midnight = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(random.Next(0, 73049));

            double step = JulianDate.FromInstant(midnight).DaysSinceJ2000 - JulianDate.FromInstant(midnight.AddTicks(-1)).DaysSinceJ2000;

            // One tick is 1.157e-12 days; at up to 36,525 days the values carry about 7e-12 days of rounding.
            Assert.Equal(1.0 / TimeSpan.TicksPerDay, step, 2e-11);
        }
    }

    [Fact]
    public void Uses_the_instant_not_the_local_clock_time()
    {
        var utc = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
        var phoenix = utc.ToOffset(TimeSpan.FromHours(-7)); // same instant, previous calendar day in Phoenix

        Assert.Equal(JulianDate.FromInstant(utc), JulianDate.FromInstant(phoenix));
    }
}
