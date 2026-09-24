using Sky.Orbital.Time;

namespace Sky.Orbital.Tests.Time;

public class SiderealTimeTests
{
    private const double RadiansToDegrees = 180.0 / Math.PI;

    [Fact]
    public void Gmst_at_J2000_is_the_defining_constant()
    {
        // IAU-82: GMST at J2000.0 UT1 is 67310.54841 s of time = 18h 41m 50.54841s = 280.46061837504 deg.
        var jd = JulianDate.FromInstant(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));

        double gmstDegrees = SiderealTime.GreenwichMean(jd) * RadiansToDegrees;

        Assert.Equal(280.46061837504, gmstDegrees, 1e-9);
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
}
