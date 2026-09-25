using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Tests.CrossCheck;

/// <summary>
/// Milestone 1's pass finder against refined Skyfield pass events for the ISS over Phoenix, 7 days
/// from 2026-09-24 04:00 UTC. The reference events use UT1 = UTC, as Sky's production path does,
/// and are refined with Skyfield's own altitude function to well under a microsecond, so the
/// bounds below are exact consequences of the finder's design, with 1 ms of slack for rounding:
/// rise is the first 10 s sample at or above 10 degrees, so 0 to 10 s late; set is the last, so
/// 0 to 10 s early; the peak is refined in 0.1 s steps, so it is within 0.1 s of the true peak and
/// at most 0.054 degrees low (the line of sight turns at most 7.7 km/s / 410 km = 1.08 deg/s).
/// </summary>
public class PassCrossCheckTests
{
    private static readonly SkyfieldReference Reference = SkyfieldReference.Instance;
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan PeakTimeBound = TimeSpan.FromSeconds(0.1);
    private const double PeakElevationShortfallDegrees = 0.06;

    [Theory]
    [InlineData(10.0, 25)]
    [InlineData(30.0, 12)]
    public void Finds_the_same_passes_as_skyfield_within_the_coarse_step(double minimumElevation, int expectedCount)
    {
        // Skyfield's list is for 10 degrees; at 30 degrees the same passes apply whose peak clears 30.
        var expected = Reference.Passes.Where(p => p.Culmination.ElevationDeg >= minimumElevation).ToList();
        Assert.Equal(expectedCount, expected.Count);

        var found = CoarsePassFinder.Find(
            Sgp4Propagator.Create(Reference.MeanElements),
            new TopocentricFrame(Reference.ObserverLocation),
            WindowStart,
            WindowStart.AddDays(7),
            minimumElevation).Passes;

        Assert.Equal(expected.Count, found.Count);
        foreach (var (sky, reference) in found.Zip(expected))
        {
            if (minimumElevation == 10.0)
            {
                Assert.InRange(sky.Rise.Time - reference.Rise.Utc, -Slack, Step + Slack);
                Assert.InRange(reference.Set.Utc - sky.Set.Time, -Slack, Step + Slack);
            }

            Assert.InRange(sky.Culmination.Time - reference.Culmination.Utc, -(PeakTimeBound + Slack), PeakTimeBound + Slack);
            Assert.InRange(
                sky.Culmination.ElevationDegrees,
                reference.Culmination.ElevationDeg - PeakElevationShortfallDegrees,
                reference.Culmination.ElevationDeg + 1e-6);
        }
    }
}
