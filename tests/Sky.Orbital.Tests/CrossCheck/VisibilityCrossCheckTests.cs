using Sky.Orbital.Astronomy;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Tests.CrossCheck;

/// <summary>
/// Sky's Sun, shadow, twilight, and visible windows against the Milestone 2 reference: the Sun from
/// JPL's DE421 through Skyfield, and the shadow from an independent line-ellipsoid quadratic. Both
/// sides use UT1 = UTC, so no UT1 difference enters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sun direction bound, 0.0115°.</b> Meeus states 0.01° for the low-accuracy apparent place,
/// his one-term nutation in longitude and obliquity included. Rotating it to Earth-fixed adds the
/// one-term equation of the equinoxes' error against IAU 2000A: the omitted nutation terms in
/// longitude sum to 2.25″ at most, times cos ε, 2.1″ or 0.0006°. IAU-82 GMST against Skyfield's
/// IAU-2006 GMST adds under 0.15″, 0.00004°. Passing UTC as Terrestrial Time moves the Sun by
/// 69.184 s × 0.041°/h = 0.0008°. Sum: 0.0115°. Both sides take UT1 = UTC, so A5 does not enter.
/// </para>
/// <para>
/// <b>Event times.</b> A Sun-direction error δ moves the shadow function by at most k²·r·δ, where r is
/// the satellite's distance from the center and k = 1/(1 − f) (the closest point lies at most k·r
/// along the stretched line, whose direction error is at most k·δ). It moves a twilight crossing by
/// δ over the rate of the Sun's elevation. Dividing by each function's rate at the event gives that
/// event's bound, plus 1 ms for Sky's root-finding and 1 µs for the reference's bisection.
/// </para>
/// </remarks>
public class VisibilityCrossCheckTests
{
    private const double SunBoundDegrees = 0.0115;

    // Meeus's stated accuracy for the apparent place, which includes his one-term nutation.
    private const double ApparentPlaceBoundDegrees = 0.010;

    // The omitted nutation terms in longitude (2.25″ at most) times cos ε, in radians.
    private const double EquationOfEquinoxesBoundRadians = 2.1 / 3600.0 * Math.PI / 180.0;
    private const double SunBoundRadians = SunBoundDegrees * Math.PI / 180.0;
    private const double StretchFactor = 1.0 / (1.0 - Wgs84.Flattening);
    private const double RootSeconds = PassFinder.TimeToleranceSeconds + 1e-6;

    private static readonly VisibilityReference Reference = VisibilityReference.Instance;
    private static readonly SkyfieldReference Elements = SkyfieldReference.Instance;
    private static readonly Sgp4Propagator Iss = Sgp4Propagator.Create(Elements.MeanElements);
    private static readonly TopocentricFrame Phoenix = new(Elements.ObserverLocation);
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sun_direction_matches_de421_within_the_error_budget_through_the_week()
    {
        foreach (var sample in Reference.SunWeek)
        {
            Vec3 sky = Sun.PositionEcef(sample.Utc);
            double angle = AngleDegrees(sky, sample.EarthFixed);
            Assert.True(angle <= SunBoundDegrees, $"{sample.Utc:O}: {angle}° from DE421.");

            // Meeus's distance is an unperturbed ellipse: the Moon moves the Earth by up to 4,700 km
            // from the Earth-Moon barycenter and the planets add a few ×1e-5 AU, all under 1e-4 AU.
            Assert.Equal(sample.EarthFixed.Length, sky.Length, 1e-4 * Sun.AstronomicalUnitKm);
        }
    }

    [Fact]
    public void Sun_apparent_place_matches_de421_from_1950_to_2049()
    {
        // Meeus's apparent place against DE421's, both of date, at the same Terrestrial Time: no
        // Earth rotation and no time-scale difference enter, so the bound is Meeus's own 0.01°.
        foreach (var sample in Reference.SunCentury)
        {
            SunPlace sky = Sun.Apparent(new Sky.Orbital.Time.JulianDate(sample.TtJdWhole, sample.TtJdFraction));
            double angle = AngleDegrees(Direction(sky.RightAscensionDegrees, sky.DeclinationDegrees), Direction(sample.RightAscensionDeg, sample.DeclinationDeg));
            Assert.True(angle <= ApparentPlaceBoundDegrees, $"JD {sample.TtJdWhole + sample.TtJdFraction}: {angle}° from DE421.");
            Assert.Equal(sample.DistanceAu, sky.DistanceAu, 1e-4);
        }

        static Vec3 Direction(double raDeg, double decDeg)
        {
            double ra = raDeg * Math.PI / 180;
            double dec = decDeg * Math.PI / 180;
            return new Vec3(Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        }
    }

    [Fact]
    public void Equation_of_the_equinoxes_matches_iau_2000a_within_the_omitted_nutation_terms()
    {
        // Checked on its own: the combined Sun-direction bound is large enough to hide a missing or
        // sign-flipped equation of the equinoxes (at most 17.2″ × cos ε = 0.0044°), and this cannot.
        foreach (var sample in Reference.SunCentury)
        {
            SunPlace sky = Sun.Apparent(new Sky.Orbital.Time.JulianDate(sample.TtJdWhole, sample.TtJdFraction));
            Assert.Equal(sample.EquationOfEquinoxesRad, sky.EquationOfEquinoxesRadians, EquationOfEquinoxesBoundRadians);
        }
    }

    [Fact]
    public void Sun_elevation_at_the_observer_matches_de421_within_the_error_budget()
    {
        foreach (var sample in Reference.SunWeek)
        {
            double sky = Visibility.SunElevationDegrees(Phoenix, sample.Utc);
            Assert.True(Math.Abs(sky - sample.AltitudeDeg!.Value) <= SunBoundDegrees, $"{sample.Utc:O}: {sky}° against {sample.AltitudeDeg}°.");
        }
    }

    [Fact]
    public void Given_the_reference_sun_skys_shadow_function_vanishes_at_every_reference_transition()
    {
        // Isolates the shadow geometry from the Sun model: with DE421's Sun at the instant the
        // reference quadratic changes state, Sky's function must be zero to within its rate (under
        // 9 km/s) times the reference's 1 µs bisection, plus Sky's and Skyfield's satellite
        // positions' 1 µm difference: 2e-5 km. And it must change sign the right way across it.
        Assert.Equal(217, Reference.Shadow.Count);
        foreach (var e in Reference.Shadow)
        {
            Vec3 sun = e.SunEarthFixed;
            Assert.True(Math.Abs(EarthShadow.Function(Satellite(e.Utc), sun)) <= 2e-5, $"{e.Utc:O}: {EarthShadow.Function(Satellite(e.Utc), sun)} km.");
            bool litAfter = EarthShadow.IsSunlit(Satellite(e.Utc.AddMilliseconds(5)), sun);
            bool litBefore = EarthShadow.IsSunlit(Satellite(e.Utc.AddMilliseconds(-5)), sun);
            Assert.Equal(e.Kind == "leaves_shadow", litAfter);
            Assert.Equal(e.Kind == "enters_shadow", litBefore);
        }
    }

    [Fact]
    public void Shadow_transitions_match_the_reference_within_each_transitions_bound()
    {
        var sky = ShadowTransitions();
        Assert.Equal(Reference.Shadow.Count, sky.Count);
        foreach (var (found, expected) in sky.Zip(Reference.Shadow))
        {
            double bound = ShadowBoundSeconds(expected.Utc);
            double difference = (found - expected.Utc).TotalSeconds;
            Assert.True(Math.Abs(difference) <= bound, $"{expected.Utc:O}: {difference} s against a bound of {bound} s.");
        }
    }

    [Fact]
    public void Twilight_crossings_match_the_reference_within_each_crossings_bound()
    {
        var sky = TwilightCrossings();
        Assert.Equal(Reference.Twilight.Count, sky.Count);
        foreach (var (found, expected) in sky.Zip(Reference.Twilight))
        {
            double bound = TwilightBoundSeconds(expected.Utc);
            double difference = (found - expected.Utc).TotalSeconds;
            Assert.True(Math.Abs(difference) <= bound, $"{expected.Utc:O}: {difference} s against a bound of {bound} s.");
        }
    }

    [Fact]
    public void Visible_windows_match_the_reference_pass_by_pass()
    {
        var passes = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), 10.0).Passes
            .Where(p => p.Rise.Time >= WindowStart)
            .ToList();
        Assert.Equal(Reference.Passes.Count, passes.Count);
        Assert.Equal(7, Reference.Passes.Count(p => p.Windows.Count > 0));

        foreach (var (pass, expected) in passes.Zip(Reference.Passes))
        {
            var windows = Visibility.Windows(pass, Iss, Phoenix);
            Assert.Equal(expected.Windows.Count, windows.Count);
            foreach (var (w, e) in windows.Zip(expected.Windows))
            {
                Assert.Equal(e.StartsBecause, Name(w.StartsBecause));
                Assert.Equal(e.EndsBecause, Name(w.EndsBecause));
                double startBound = BoundFor(w.StartsBecause, e.StartUtc);
                double endBound = BoundFor(w.EndsBecause, e.EndUtc);
                Assert.True(Math.Abs((w.Start.Time - e.StartUtc).TotalSeconds) <= startBound, $"Start {e.StartUtc:O}: {(w.Start.Time - e.StartUtc).TotalSeconds} s against {startBound} s.");
                Assert.True(Math.Abs((w.End.Time - e.EndUtc).TotalSeconds) <= endBound, $"End {e.EndUtc:O}: {(w.End.Time - e.EndUtc).TotalSeconds} s against {endBound} s.");

                // The highest point is the culmination or an end of the window. At an end, elevation
                // differs by at most the line-of-sight rate (under 1.1°/s for the ISS here) times that
                // end's time bound.
                double elevationBound = (1.1 * Math.Max(startBound, endBound)) + 1e-6;
                Assert.Equal(e.Highest.ElevationDeg, w.Highest.ElevationDegrees, elevationBound);
            }
        }
    }

    [Fact]
    public void Skyfields_spherical_shadow_differs_from_the_ellipsoid_only_within_the_model_difference()
    {
        // Not a check of Sky's accuracy: Skyfield's is_sunlit models the Earth as a sphere. The
        // ellipsoid's surface lies between the polar and equatorial radii, so the models' shadow
        // functions differ by at most a − b = 21.4 km (times k), plus the difference in the Sun both
        // use (under 0.0115°, plus 0.0057° because Skyfield's is_sunlit uses the geometric Sun). Each transition may move by that over the function's rate.
        Assert.Equal(Reference.Shadow.Count, Reference.SkyfieldSphereShadow.Count);
        foreach (var (sphere, ellipsoid) in Reference.SkyfieldSphereShadow.Zip(Reference.Shadow))
        {
            Assert.Equal(ellipsoid.Kind, sphere.Kind);
            double rate = ShadowRate(ellipsoid.Utc);
            double sunDifference = SunBoundRadians + (0.0057 * Math.PI / 180.0);
            double bound = (((Wgs84.EquatorialRadius * Wgs84.Flattening * StretchFactor) + (StretchFactor * StretchFactor * Satellite(ellipsoid.Utc).Length * sunDifference)) / rate) + 2e-6;
            Assert.True(Math.Abs((sphere.Utc - ellipsoid.Utc).TotalSeconds) <= bound, $"{ellipsoid.Utc:O}: sphere {sphere.Utc:O}, bound {bound} s.");
        }
    }

    internal static List<DateTimeOffset> ShadowTransitions() =>
        Visibility.SignChanges(s => EarthShadow.Function(Satellite(At(s)), Sun.PositionEcef(At(s))), _ => 9.0, 7 * 86400.0)
            .Select(At).ToList();

    internal static List<DateTimeOffset> TwilightCrossings() =>
        Visibility.SignChanges(s => Visibility.SunElevationDegrees(Phoenix, At(s)) + 6.0, _ => 0.0043, 7 * 86400.0)
            .Select(At).ToList();

    internal static double ShadowBoundSeconds(DateTimeOffset t) =>
        (StretchFactor * StretchFactor * Satellite(t).Length * SunBoundRadians / ShadowRate(t)) + RootSeconds;

    internal static double TwilightBoundSeconds(DateTimeOffset t)
    {
        double rate = Math.Abs(Visibility.SunElevationDegrees(Phoenix, t.AddSeconds(30)) - Visibility.SunElevationDegrees(Phoenix, t.AddSeconds(-30))) / 60.0;
        return (SunBoundDegrees / rate) + RootSeconds;
    }

    private static double BoundFor(VisibilityChange change, DateTimeOffset t) => change switch
    {
        VisibilityChange.Rise or VisibilityChange.Set => RootSeconds + 1e-5,
        VisibilityChange.EntersShadow or VisibilityChange.LeavesShadow => ShadowBoundSeconds(t),
        _ => TwilightBoundSeconds(t),
    };

    private static double ShadowRate(DateTimeOffset t)
    {
        // The shadow function's rate at the event, by a central difference over ±0.5 s; the
        // function is smooth there, so this is accurate to well under a percent.
        double after = EarthShadow.Function(Satellite(t.AddSeconds(0.5)), Sun.PositionEcef(t.AddSeconds(0.5)));
        double before = EarthShadow.Function(Satellite(t.AddSeconds(-0.5)), Sun.PositionEcef(t.AddSeconds(-0.5)));
        return Math.Abs(after - before);
    }

    private static string Name(VisibilityChange change) => change switch
    {
        VisibilityChange.Rise => "rise",
        VisibilityChange.Set => "set",
        VisibilityChange.LeavesShadow => "leaves_shadow",
        VisibilityChange.EntersShadow => "enters_shadow",
        VisibilityChange.SkyDarkens => "sky_darkens",
        VisibilityChange.SkyBrightens => "sky_brightens",
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    private static Vec3 Satellite(DateTimeOffset t) => EarthRotation.TemeToEcef(Iss.Propagate(t).State, t).Position;

    private static DateTimeOffset At(double seconds) => WindowStart.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    private static double AngleDegrees(Vec3 a, Vec3 b)
    {
        // atan2 of the cross and dot products keeps full precision for small angles.
        var cross = new Vec3((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));
        return Math.Atan2(cross.Length, a.Dot(b)) * 180.0 / Math.PI;
    }
}
