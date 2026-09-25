using System.Globalization;
using System.Text;
using Sky.Orbital.Frames;
using Sky.Orbital.Propagation;
using Sky.Orbital.Time;

namespace Sky.Orbital.Tests.CrossCheck;

/// <summary>
/// Sky's full pipeline against Skyfield for the ISS over Phoenix: 133 instants over 3 days,
/// including every 20 s through a pass that peaks at 68.8 degrees. Skyfield runs Vallado's
/// C++ SGP4 and does its own frame conversions, so agreement here is independent evidence.
/// </summary>
/// <remarks>
/// Tolerances come from analysis done before these tests first ran. Both sides use the same
/// SGP4 algorithm and the same TEME to Earth-fixed rotation, so position differences should be
/// far below a millimeter. The one known modeling difference is the Earth rotation rate used
/// for velocities: Sky uses the GMST rate (7.2921158553e-5 rad/s) and Skyfield the IERS nominal
/// rate (7.2921150e-5). That is 8.6e-12 rad/s, which is up to 5.8e-8 km/s in Earth-fixed
/// velocity at the ISS's radius and up to 4.6e-8 km/s in range rate from Phoenix. Velocity
/// tolerances are 1e-7 km/s to cover it.
/// </remarks>
public class SkyfieldCrossCheckTests
{
    private static readonly SkyfieldReference Reference = SkyfieldReference.Instance;
    private static readonly Sgp4Propagator Propagator = Sgp4Propagator.Create(Reference.MeanElements);
    private static readonly TopocentricFrame Observer = new(Reference.ObserverLocation);

    [Fact]
    public void Reference_file_covers_three_days_and_a_full_pass()
    {
        Assert.Equal(133, Reference.States.Count);
        Assert.True(Reference.States.Max(s => s.Look.ElevationDeg) > 68.0);
    }

    [Fact]
    public void Teme_state_matches_python_sgp4()
    {
        // Same algorithm; 2e-7 km and km/s, as in the Vallado verification.
        AssertAll((s, failures) =>
        {
            var teme = Propagator.Propagate(s.Utc).State;
            Check(failures, s, "TEME position", Distance(teme.Position, Vector(s.TemeKm)), 2e-7);
            Check(failures, s, "TEME velocity", Distance(teme.Velocity, Vector(s.TemeKmPerSecond)), 2e-7);
        });
    }

    [Fact]
    public void Earth_fixed_state_matches_skyfield_given_the_same_ut1()
    {
        AssertAll((s, failures) =>
        {
            var ecef = EarthFixed(s, s.Ut1MinusUtcSeconds);
            Check(failures, s, "Earth-fixed position", Distance(ecef.Position, Vector(s.EarthFixedKm)), 1e-6);
            Check(failures, s, "Earth-fixed velocity", Distance(ecef.Velocity, Vector(s.EarthFixedKmPerSecond)), 1e-7);
        });
    }

    [Fact]
    public void Subpoint_matches_the_exact_geodetic_solution_given_the_same_ut1()
    {
        // 1e-8 degrees is about 1 mm on the ground, consistent with the 1 mm position tolerance.
        // Latitude and height are compared with Skyfield's formula run to convergence. Skyfield's
        // own values stop after three iterations and fall up to 2e-8 degrees short; the file keeps
        // them for transparency, and the generator refuses to run if that gap exceeds 1e-7.
        AssertAll((s, failures) =>
        {
            var g = Wgs84.FromEcef(EarthFixed(s, s.Ut1MinusUtcSeconds).Position);
            Check(failures, s, "subpoint latitude", Math.Abs(g.LatitudeDegrees - s.Subpoint.LatitudeConvergedDeg), 1e-8);
            Check(failures, s, "subpoint longitude", Math.Abs(AngleDifference(g.LongitudeDegrees, s.Subpoint.LongitudeDeg)), 1e-8);
            Check(failures, s, "subpoint height", Math.Abs(g.HeightKm - s.Subpoint.HeightConvergedKm), 1e-6);
        });
    }

    [Fact]
    public void Look_angles_match_skyfield_given_the_same_ut1()
    {
        // 1e-6 degrees is 7 mm at 400 km, far above the expected sub-millimeter agreement.
        AssertAll((s, failures) =>
        {
            var look = Observer.LookAt(EarthFixed(s, s.Ut1MinusUtcSeconds));
            Check(failures, s, "azimuth", Math.Abs(AngleDifference(look.AzimuthDegrees, s.Look.AzimuthDeg)), 1e-6);
            Check(failures, s, "elevation", Math.Abs(look.ElevationDegrees - s.Look.ElevationDeg), 1e-6);
            Check(failures, s, "range", Math.Abs(look.RangeKm - s.Look.RangeKm), 1e-6);
            Check(failures, s, "range rate", Math.Abs(look.RangeRateKmPerSecond - s.Look.RangeRateKmPerSecond), 1e-7);
        });
    }

    [Fact]
    public void Treating_utc_as_ut1_changes_results_only_by_earth_rotation_over_that_interval()
    {
        // Assumption A5 as Sky runs in production (UT1 - UTC taken as zero). Skipping the
        // correction rotates the Earth by delta = rate * dUT1, which moves the satellite
        // relative to the Earth by up to delta * (its distance from the spin axis). Each bound
        // below is that displacement's worst-case effect plus the same-UT1 tolerance above.
        AssertAll((s, failures) =>
        {
            var ecef = EarthFixed(s, 0.0);
            var reference = Vector(s.EarthFixedKm);
            double delta = SiderealTime.GreenwichMeanRate(JulianDate.FromInstant(s.Utc)) * Math.Abs(s.Ut1MinusUtcSeconds);
            double shift = delta * Math.Sqrt((reference.X * reference.X) + (reference.Y * reference.Y));
            double speed = Vector(s.EarthFixedKmPerSecond).Length;

            Check(failures, s, "Earth-fixed position", Distance(ecef.Position, reference), (shift * 1.001) + 1e-6);

            var look = Observer.LookAt(ecef);
            double angleBoundDegrees = (shift / s.Look.RangeKm * 180.0 / Math.PI * 1.001) + 1e-6;
            double horizontal = s.Look.RangeKm * Math.Cos(s.Look.ElevationDeg * Math.PI / 180.0);
            double azimuthBoundDegrees = (shift / horizontal * 180.0 / Math.PI * 1.001) + 1e-6;
            Check(failures, s, "azimuth", Math.Abs(AngleDifference(look.AzimuthDegrees, s.Look.AzimuthDeg)), azimuthBoundDegrees);
            Check(failures, s, "elevation", Math.Abs(look.ElevationDegrees - s.Look.ElevationDeg), angleBoundDegrees);
            Check(failures, s, "range", Math.Abs(look.RangeKm - s.Look.RangeKm), (shift * 1.001) + 1e-6);
            double rangeRateBound = (((shift * speed / s.Look.RangeKm) + (delta * speed)) * 1.001) + 1e-7;
            Check(failures, s, "range rate", Math.Abs(look.RangeRateKmPerSecond - s.Look.RangeRateKmPerSecond), rangeRateBound);
        });
    }

    private static EcefState EarthFixed(SkyfieldReference.StateRecord s, double ut1MinusUtc) =>
        EarthRotation.TemeToEcef(Propagator.Propagate(s.Utc).State, s.Utc, ut1MinusUtc);

    private static void AssertAll(Action<SkyfieldReference.StateRecord, StringBuilder> check)
    {
        var failures = new StringBuilder();
        foreach (var state in Reference.States)
        {
            check(state, failures);
        }

        Assert.True(failures.Length == 0, $"Differences from Skyfield:\n{failures}");
    }

    private static void Check(StringBuilder failures, SkyfieldReference.StateRecord s, string what, double difference, double tolerance)
    {
        if (!(difference <= tolerance))
        {
            failures.AppendLine(CultureInfo.InvariantCulture, $"{s.Utc:O} {what}: {difference:E3} exceeds {tolerance:E3}");
        }
    }

    private static Vec3 Vector(double[] v) => new(v[0], v[1], v[2]);

    private static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

    private static double AngleDifference(double a, double b) => ((((b - a) % 360.0) + 540.0) % 360.0) - 180.0;
}
