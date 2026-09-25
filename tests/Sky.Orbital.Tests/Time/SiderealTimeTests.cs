using Sky.Orbital.Time;

namespace Sky.Orbital.Tests.Time;

public class SiderealTimeTests
{
    private const double RadiansToDegrees = 180.0 / Math.PI;

    [Fact]
    public void Gmst_at_J2000_is_the_defining_constant()
    {
        // IAU-82: GMST at J2000.0 UT1 is 67310.54841 s of time = 18h 41m 50.54841s; 67310.54841 / 240
        // = 280.460618375 deg exactly.
        var jd = JulianDate.FromInstant(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));

        double gmstDegrees = SiderealTime.GreenwichMean(jd) * RadiansToDegrees;

        Assert.Equal(280.460618375, gmstDegrees, 1e-10);
    }

    [Fact]
    public void Gmst_matches_vallado_example_3_5()
    {
        // Vallado, Fundamentals of Astrodynamics and Applications, Example 3-5:
        // 1992 August 20 12:14 UT1 gives GMST = 152.5787878 deg. The date is before J2000,
        // so this also checks wrapping a negative angle into [0, 360).
        var jd = JulianDate.FromInstant(new DateTimeOffset(1992, 8, 20, 12, 14, 0, TimeSpan.Zero));

        double gmstDegrees = SiderealTime.GreenwichMean(jd) * RadiansToDegrees;

        Assert.Equal(152.5787878, gmstDegrees, 1e-6);
    }

    [Fact]
    public void Gmst_matches_the_teme_example_in_vallado_2006()
    {
        // AIAA 2006-6753 Appendix C: UT1 = 2004-04-06 07:51:27.946047 gives GMST = 312.8098943 deg.
        var ut1 = new DateTimeOffset(2004, 4, 6, 7, 51, 27, TimeSpan.Zero).AddTicks(9_460_470);

        double gmstDegrees = SiderealTime.GreenwichMean(JulianDate.FromInstant(ut1)) * RadiansToDegrees;

        Assert.Equal(312.8098943, gmstDegrees, 1e-6);
    }

    // d/dt of the IAU-82 expression at T = 0: (2 pi / 86400 s) * (3164400184.812866 / 3155760000),
    // derived with exact rational arithmetic.
    private const double RateAtJ2000RadiansPerSecond = 7.29211585530659e-5;

    [Fact]
    public void Gmst_rate_at_J2000_is_the_derivative_of_the_iau82_formula()
    {
        // This exceeds the Earth's inertial rotation rate (Vallado's 7.29211514670698e-5 rad/s) by
        // 7.086e-12 rad/s, which is the precession of the mean equinox: 46.12 arcseconds per year.
        var jd = JulianDate.FromInstant(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(RateAtJ2000RadiansPerSecond, SiderealTime.GreenwichMeanRate(jd), 1e-18);
    }

    [Fact]
    public void Gmst_advances_360_985647366_degrees_per_ut1_day_around_J2000()
    {
        // One UT1 day centered on J2000: 360 deg * 1.002737909350795 = 360.985647366286 deg.
        // The T^2 term cancels by symmetry and the T^3 term is below 1e-20 deg.
        var before = JulianDate.FromInstant(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var after = JulianDate.FromInstant(new DateTimeOffset(2000, 1, 2, 0, 0, 0, TimeSpan.Zero));

        double advanceDegrees = (SiderealTime.GreenwichMean(after) - SiderealTime.GreenwichMean(before)) * RadiansToDegrees;
        if (advanceDegrees < 0)
        {
            advanceDegrees += 360.0;
        }

        Assert.Equal(0.985647366286, advanceDegrees, 1e-10);
    }

    [Fact]
    public void Gmst_has_no_rounding_noise_in_one_second_steps_from_1970_to_2070()
    {
        // Over 1 s, GMST must advance by the rate times 1 s. The rate varies by under 3e-15 rad/s
        // across 1970-2070, so the J2000 rate is the expected value throughout. The 5e-13 rad
        // tolerance is 3.5 nm at low-Earth-orbit radius; it fails any implementation that reduces
        // a large count of seconds modulo 2 pi, which carries about 5e-11 rad of rounding noise.
        var random = new Random(3);
        var start = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 2000; i++)
        {
            var t0 = start.AddTicks((long)(random.NextDouble() * 100 * 365.25 * TimeSpan.TicksPerDay));
            double g0 = SiderealTime.GreenwichMean(JulianDate.FromInstant(t0));
            double g1 = SiderealTime.GreenwichMean(JulianDate.FromInstant(t0.AddSeconds(1)));

            double step = g1 - g0;
            if (step < 0)
            {
                step += 2 * Math.PI;
            }

            Assert.Equal(RateAtJ2000RadiansPerSecond, step, 5e-13);
        }
    }
}

