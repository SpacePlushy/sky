using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Tests.CrossCheck;

/// <summary>
/// The pass finder against refined Skyfield pass events for the ISS over Phoenix, 7 days from
/// 2026-09-24 04:00 UTC. The reference events use UT1 = UTC, as Sky's production path does, and are
/// refined with Skyfield's own altitude function: rise and set to under a microsecond, peaks to
/// tens of microseconds.
/// </summary>
/// <remarks>
/// The bounds follow from the finder's design. Rise and set: Brent's method places each within
/// 1 ms of Sky's own crossing, and Sky's and Skyfield's elevations agree to 5×10⁻¹¹° (the full
/// pipeline check), which moves a crossing by under a microsecond even on the slowest rise here;
/// 10 µs covers that and the reference's own refinement. Peak time: Brent places the maximum
/// within 2 (10⁻⁴ + 1.5×10⁻⁷) s, and the reference is good to tens of microseconds, so 0.3 ms.
/// Peak elevation: Sky's is low by at most the pass's stated uncertainty, and never high.
/// </remarks>
public class PassCrossCheckTests
{
    private static readonly SkyfieldReference Reference = SkyfieldReference.Instance;
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan CrossingBound = TimeSpan.FromSeconds(PassFinder.TimeToleranceSeconds) + TimeSpan.FromTicks(100);
    private static readonly TimeSpan PeakTimeBound = TimeSpan.FromSeconds(3e-4);

    [Theory]
    [InlineData(10.0, 25)]
    [InlineData(30.0, 12)]
    public void Finds_the_same_passes_as_skyfield_within_the_root_finding_tolerance(double minimumElevation, int expectedCount)
    {
        // Skyfield's list is for 10 degrees; at 30 degrees the same passes apply whose peak clears 30.
        var expected = Reference.Passes.Where(p => p.Culmination.ElevationDeg >= minimumElevation).ToList();
        Assert.Equal(expectedCount, expected.Count);

        var found = PassFinder.Find(
            Sgp4Propagator.Create(Reference.MeanElements),
            new TopocentricFrame(Reference.ObserverLocation),
            WindowStart,
            WindowStart.AddDays(7),
            minimumElevation).Passes
            .Where(p => p.Rise.Time >= WindowStart) // Skyfield's search leaves out the pass already up at the start
            .ToList();

        Assert.Equal(expected.Count, found.Count);
        foreach (var (sky, reference) in found.Zip(expected))
        {
            if (minimumElevation == 10.0)
            {
                Assert.InRange(sky.Rise.Time - reference.Rise.Utc, -CrossingBound, CrossingBound);
                Assert.InRange(sky.Set.Time - reference.Set.Utc, -CrossingBound, CrossingBound);
                Assert.Equal(reference.Rise.AzimuthDeg, sky.Rise.AzimuthDegrees, 1e-3);
                Assert.Equal(reference.Set.AzimuthDeg, sky.Set.AzimuthDegrees, 1e-3);
            }

            Assert.InRange(sky.Culmination.Time - reference.Culmination.Utc, -PeakTimeBound, PeakTimeBound);
            Assert.InRange(
                sky.Culmination.ElevationDegrees,
                reference.Culmination.ElevationDeg - sky.PeakElevationUncertaintyDegrees - 1e-9,
                reference.Culmination.ElevationDeg + 1e-9);
        }
    }
}
