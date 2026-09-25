using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Tests.CrossCheck;

/// <summary>
/// Milestone 1's coarse pass finder against Skyfield's pass events for the ISS over Phoenix,
/// 7 days from 2026-09-24 04:00 UTC. The finder samples every 10 s, so its bounds follow from
/// the step: rise is reported up to one step late, set up to one step early, and the peak
/// within one step. Skyfield's events are root-found; Sky treats UTC as UT1 while Skyfield's
/// data used UT1 - UTC = +0.096 s, which moves crossings by under 0.05 s. The half-second
/// slack below covers that.
/// </summary>
public class PassCrossCheckTests
{
    private static readonly SkyfieldReference Reference = SkyfieldReference.Instance;
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(0.5);

    // Peak elevation can fall below the true maximum by the elevation drop within half a step of
    // the peak. Skyfield's samples around the 68.8 degree pass show curvature up to 0.0202 deg/s^2,
    // so the drop within 5 s is at most 0.506 degrees. Tolerance 0.6 degrees.
    private const double PeakElevationShortfallDegrees = 0.6;

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
            minimumElevation);

        Assert.Equal(expected.Count, found.Count);
        foreach (var (sky, reference) in found.Zip(expected))
        {
            if (minimumElevation == 10.0)
            {
                Assert.InRange(sky.Rise.Time - reference.Rise.Utc, -Slack, Step + Slack);
                Assert.InRange(reference.Set.Utc - sky.Set.Time, -Slack, Step + Slack);
            }

            Assert.InRange(sky.Culmination.Time - reference.Culmination.Utc, -(Step + Slack), Step + Slack);
            Assert.InRange(
                sky.Culmination.ElevationDegrees,
                reference.Culmination.ElevationDeg - PeakElevationShortfallDegrees,
                reference.Culmination.ElevationDeg + 0.01);
        }
    }
}
