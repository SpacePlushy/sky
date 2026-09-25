using Sky.Orbital.Frames;
using Sky.Orbital.Propagation;
using Sky.Orbital.Time;

namespace Sky.Orbital.Tests.Frames;

/// <summary>
/// TEME to Earth-fixed, checked against the worked example in Vallado et al. 2006,
/// "Revisiting Spacetrack Report #3", AIAA 2006-6753 Rev 2, Appendix C.
/// </summary>
public class EarthRotationTests
{
    // The example is stated as UTC 2004-04-06 07:51:28.386009 with UT1 - UTC = -0.4399619 s
    // (Vallado, Seago, and Seidelmann, the paper's companion), so UT1 = 07:51:27.9460471. The
    // reference computation held UT1 as one double: the nearest double to that instant is
    // 2453101.8274067831225693..., printed in the paper as 2453101.82740678310, which is 14.69
    // microseconds late because a double near JD 2.45e6 resolves only 40.2 microseconds. To
    // compare like with like, these tests give Sky that same instant. Sky's split Julian date
    // represents it exactly (the subtraction below is exact).
    private const double ReferenceJulianDateUt1 = 2453101.82740678310;
    private static readonly JulianDate ReferenceUt1 = new(2453101.5, ReferenceJulianDateUt1 - 2453101.5);

    private static readonly DateTimeOffset ExampleUtc =
        new DateTimeOffset(2004, 4, 6, 7, 51, 28, TimeSpan.Zero).AddTicks(3_860_090);

    private const double ExampleUt1MinusUtcSeconds = -0.4399619;

    private static readonly TemeState ExampleTeme = new(
        new Vec3(5094.18016210, 6127.64465950, 6380.34453270),
        new Vec3(-4.746131487, 0.785818041, 5.531931288));

    // Published polar motion for the example, in arcseconds.
    private const double XpArcseconds = -0.140682;
    private const double YpArcseconds = 0.333309;
    private const double ArcsecondsPerRadian = 206264.80624709636;

    [Fact]
    public void Matches_the_published_itrf_position_once_polar_motion_is_applied()
    {
        var ecef = EarthRotation.TemeToEcef(ExampleTeme, ReferenceUt1);

        var itrf = PefToItrf(ecef.Position);

        // Published to 1e-8 km. Tolerance 0.1 mm.
        AssertClose(new Vec3(-1033.47938300, 7901.29527540, 6380.35659580), itrf, 1e-7);
    }

    [Fact]
    public void Matches_the_published_itrf_velocity_once_polar_motion_is_applied()
    {
        var ecef = EarthRotation.TemeToEcef(ExampleTeme, ReferenceUt1);

        var itrf = PefToItrf(ecef.Velocity);

        // Published to 1e-9 km/s. Tolerance 0.1 mm/s. Expect about 0.066 mm/s in x: Sky subtracts
        // Earth rotation at the GMST rate, 7.29211585531e-5 rad/s, so velocity is exactly the
        // derivative of position. The paper uses the inertial rate scaled by (1 - LOD/86400) with
        // LOD = 0.0015563 s, 7.29211501535e-5 rad/s. The difference, 8.40e-12 rad/s, times
        // y = 7901.3 km is 0.0664 mm/s.
        AssertClose(new Vec3(-3.225636520, -2.872451450, 5.531924446), itrf, 1e-7);
    }

    [Fact]
    public void Split_julian_date_keeps_the_instant_the_reference_double_shifts_by_14_69_microseconds()
    {
        // Documents why the tests above use the reference's Julian date rather than the stated
        // UTC and dUT1. A single-double Julian date would show 0 or 40.2 microseconds here.
        var exactUt1 = JulianDate.FromInstant(ExampleUtc.AddTicks(-4_399_619)); // UTC - 0.4399619 s

        double referenceDaysSinceJ2000 = ReferenceJulianDateUt1 - 2451545.0; // exact: Sterbenz lemma
        double microseconds = (referenceDaysSinceJ2000 - exactUt1.DaysSinceJ2000) * 86400e6;

        Assert.Equal(14.690, microseconds, 0.05);
    }

    [Fact]
    public void Ignoring_polar_motion_costs_no_more_than_the_pole_offset_angle_times_radius()
    {
        // Assumption A4. The example's combined pole offset is sqrt(xp^2 + yp^2) = 0.36178",
        // 1.7539e-6 rad. At this satellite's 10,208 km radius that bounds the gap between Sky's
        // frame and full ITRF at 17.9 m. On the ground or in low orbit it is about 11 to 12 m.
        var itrf = new Vec3(-1033.47938300, 7901.29527540, 6380.35659580);
        double poleOffsetRadians = Math.Sqrt((XpArcseconds * XpArcseconds) + (YpArcseconds * YpArcseconds)) / ArcsecondsPerRadian;

        var ecef = EarthRotation.TemeToEcef(ExampleTeme, ReferenceUt1);

        double differenceKm = Length(Subtract(ecef.Position, itrf));
        Assert.InRange(differenceKm, 0.0, poleOffsetRadians * Length(ecef.Position));
    }

    [Fact]
    public void Treating_utc_as_ut1_rotates_the_earth_by_the_skipped_interval()
    {
        // Assumption A5. Leaving out the -0.4399619 s correction rotates the Earth 0.4399619 s too
        // far: 7.29212e-5 rad/s * 0.4399619 s * 7968.6 km (distance from the spin axis) = 0.2557 km.
        var withUt1 = EarthRotation.TemeToEcef(ExampleTeme, ExampleUtc, ExampleUt1MinusUtcSeconds);
        var utcAsUt1 = EarthRotation.TemeToEcef(ExampleTeme, ExampleUtc);

        Assert.Equal(0.2557, Length(Subtract(withUt1.Position, utcAsUt1.Position)), 1e-4);
    }

    /// <summary>
    /// Takes a pseudo Earth-fixed vector to ITRF with the published polar motion, using Vallado's
    /// IAU-76/FK5 polar motion matrix: v_itrf = PM^T v_pef (Vallado, polarm and teme2ecef).
    /// Sky itself ignores polar motion (assumption A4). This helper exists only so the tests can
    /// compare against the published ITRF result, which is computed forward at 8 decimals.
    /// </summary>
    private static Vec3 PefToItrf(Vec3 v)
    {
        double xp = XpArcseconds / ArcsecondsPerRadian;
        double yp = YpArcseconds / ArcsecondsPerRadian;
        double cosXp = Math.Cos(xp), sinXp = Math.Sin(xp), cosYp = Math.Cos(yp), sinYp = Math.Sin(yp);
        return new Vec3(
            (cosXp * v.X) + (sinXp * sinYp * v.Y) + (sinXp * cosYp * v.Z),
            (cosYp * v.Y) - (sinYp * v.Z),
            (-sinXp * v.X) + (cosXp * sinYp * v.Y) + (cosXp * cosYp * v.Z));
    }

    private static Vec3 Subtract(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static double Length(Vec3 v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));

    private static void AssertClose(Vec3 expected, Vec3 actual, double tolerance) =>
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance
                && Math.Abs(expected.Y - actual.Y) <= tolerance
                && Math.Abs(expected.Z - actual.Z) <= tolerance,
            $"Expected {expected}, got {actual}, tolerance {tolerance}.");
}
