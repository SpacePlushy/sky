using System.Globalization;
using System.Text;
using Sky.Orbital.Elements;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Tests.Propagation;

/// <summary>
/// Checks Sky's SGP4 against Vallado's published verification set (AIAA 2006-6753).
/// The reference output was generated in improved mode 'i' with WGS-72 constants.
/// </summary>
public class Sgp4VerificationTests
{
    // The reference file prints 8 decimals of km and 9 of km/s. python-sgp4 compares
    // against it with the same 2e-7 bound, about 0.2 mm and 0.2 mm/s.
    private const double PositionToleranceKm = 2e-7;
    private const double VelocityToleranceKmPerSecond = 2e-7;

    // Satellite 33334 fails at initialization. The driver still printed a t=0 line, but it
    // is a stale copy of the previous satellite's last state, so it is not a reference value.
    private const long CatalogNumberWithStaleLine = 33334;

    public static TheoryData<string> AllRuns => new(ValladoVerificationData.Runs.Select(r => r.Key));

    [Fact]
    public void Reference_file_holds_every_published_run_and_state()
    {
        Assert.Equal(33, ValladoVerificationData.Runs.Count);
        Assert.Equal(667, ValladoVerificationData.Runs.Sum(r => r.States.Count));
    }

    [Theory]
    [MemberData(nameof(AllRuns))]
    public void Reproduces_every_reference_state(string key)
    {
        var run = ValladoVerificationData.Get(key);
        var states = run.CatalogNumber == CatalogNumberWithStaleLine ? [] : run.States;
        var propagator = Sgp4Propagator.Create(Tle.Parse(run.Line1, run.Line2), OperationMode.Improved);

        var failures = new StringBuilder();
        foreach (var expected in states)
        {
            var result = propagator.Propagate(expected.Minutes);
            if (!result.Succeeded)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"t={expected.Minutes} min: SGP4 error {result.Error}");
                continue;
            }

            double dr = Distance(expected.Position, result.State.Position);
            double dv = Distance(expected.Velocity, result.State.Velocity);
            if (MaxComponentError(expected.Position, result.State.Position) > PositionToleranceKm
                || MaxComponentError(expected.Velocity, result.State.Velocity) > VelocityToleranceKmPerSecond)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"t={expected.Minutes} min: |dr|={dr:E2} km, |dv|={dv:E2} km/s");
            }
        }

        Assert.True(failures.Length == 0, $"Run {key} differs from tcppver.out:\n{failures}");
    }

    public static TheoryData<string, Sgp4Error> RunsThatEndInError => new()
    {
        { "12 22312", Sgp4Error.MeanEccentricityOutOfRange },
        { "23 28350", Sgp4Error.MeanEccentricityOutOfRange },
        { "26 28872", Sgp4Error.Decayed },
        { "27 29141", Sgp4Error.Decayed },
        { "30 33333", Sgp4Error.SemiLatusRectumNegative },
        { "33 20413", Sgp4Error.Decayed },
    };

    [Theory]
    [MemberData(nameof(RunsThatEndInError))]
    public void Reports_the_published_error_at_the_step_after_the_last_reference_state(string key, Sgp4Error expected)
    {
        var run = ValladoVerificationData.Get(key);
        var propagator = Sgp4Propagator.Create(Tle.Parse(run.Line1, run.Line2), OperationMode.Improved);
        double failingMinutes = Math.Min(run.States[^1].Minutes + run.StepMinutes, run.StopMinutes);

        var result = propagator.Propagate(failingMinutes);

        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1440.0)]
    public void Reports_initialization_failure_at_every_time(double minutes)
    {
        // Run 31 (satellite 33334) has a perturbed eccentricity out of range at epoch.
        var run = ValladoVerificationData.Get("31 33334");
        var propagator = Sgp4Propagator.Create(Tle.Parse(run.Line1, run.Line2), OperationMode.Improved);

        var result = propagator.Propagate(minutes);

        Assert.Equal(Sgp4Error.PerturbedEccentricityOutOfRange, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.State);
    }

    [Fact]
    public void Propagating_to_an_instant_matches_the_reference_state_at_that_offset()
    {
        // Run 01 (satellite 00005), reference line for t = 360 min in tcppver.out.
        var run = ValladoVerificationData.Get("01 00005");
        var elements = Tle.Parse(run.Line1, run.Line2);
        var propagator = Sgp4Propagator.Create(elements);

        var state = propagator.Propagate(elements.Epoch.AddMinutes(360)).State;

        AssertClose(new Vec3(-7154.03120202, -3783.17682504, -3536.19412294), state.Position, PositionToleranceKm);
        AssertClose(new Vec3(4.741887409, -4.151817765, -2.093935425), state.Velocity, VelocityToleranceKmPerSecond);
    }

    [Fact]
    public void Improved_and_afspc_modes_agree_exactly_for_a_near_earth_orbit()
    {
        // The mode only changes deep-space terms, so the ISS must be unaffected (assumption A2).
        var iss = IssElements20260924;
        var improved = Sgp4Propagator.Create(iss, OperationMode.Improved);
        var afspc = Sgp4Propagator.Create(iss, OperationMode.Afspc);

        for (double minutes = 0; minutes <= 7 * 1440; minutes += 1)
        {
            Assert.Equal(improved.Propagate(minutes).State, afspc.Propagate(minutes).State);
        }
    }

    [Fact]
    public void Improved_and_afspc_modes_differ_for_a_low_inclination_deep_space_orbit()
    {
        // Run 16 (satellite 23599) has a 322-minute period and 6.9 degree inclination, so SGP4
        // applies the Lyddane modification, where AFSPC mode handles the node quadrant
        // differently. The paths diverge by up to about 1 km over the published schedule.
        // A difference proves the mode setting reaches the SGP4 code.
        var run = ValladoVerificationData.Get("16 23599");
        var elements = Tle.Parse(run.Line1, run.Line2);
        var improved = Sgp4Propagator.Create(elements, OperationMode.Improved);
        var afspc = Sgp4Propagator.Create(elements, OperationMode.Afspc);

        double largestDifferenceKm = 0;
        for (double minutes = run.StartMinutes; minutes <= run.StopMinutes; minutes += run.StepMinutes)
        {
            largestDifferenceKm = Math.Max(
                largestDifferenceKm,
                Distance(improved.Propagate(minutes).State.Position, afspc.Propagate(minutes).State.Position));
        }

        Assert.True(largestDifferenceKm > 0.1, $"Largest difference was only {largestDifferenceKm} km.");
    }

    // ISS (ZARYA) from CelesTrak GROUP=stations, downloaded 2026-09-24.
    private static readonly MeanElements IssElements20260924 = new()
    {
        CatalogNumber = 25544,
        Epoch = new DateTimeOffset(2026, 9, 24, 3, 24, 21, TimeSpan.Zero).AddTicks(4_525_440),
        MeanMotion = 15.49258637,
        Eccentricity = 0.00046914,
        Inclination = 51.6318,
        RightAscensionOfAscendingNode = 170.3464,
        ArgumentOfPericenter = 174.6338,
        MeanAnomaly = 185.4701,
        BStar = 0.00018115501,
        MeanMotionDot = 9.634e-5,
        MeanMotionDdot = 0,
    };

    private static double Distance(Vec3 a, Vec3 b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    private static double MaxComponentError(Vec3 a, Vec3 b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

    private static void AssertClose(Vec3 expected, Vec3 actual, double tolerance) =>
        Assert.True(
            MaxComponentError(expected, actual) <= tolerance,
            $"Expected {expected}, got {actual}, tolerance {tolerance}.");
}
