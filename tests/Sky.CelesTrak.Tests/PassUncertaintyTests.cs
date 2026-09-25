using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.CelesTrak.Tests;

/// <summary>
/// The pass finder's per-pass peak uncertainty, checked on real CelesTrak element sets including the
/// lowest orbit in the stations group, with elements up to 29 days old (the CLI searches up to 30
/// days ahead). Each case is an exactly overhead pass, where elevation has a corner at 90 degrees:
/// the worst case for the minimizer.
/// </summary>
public class PassUncertaintyTests
{
    private static readonly IReadOnlyList<GpRecord> Stations =
        OmmParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations-2026-09-24.json")));

    [Theory]
    [InlineData(25544L, 1.0)]  // ISS
    [InlineData(25544L, 15.0)]
    [InlineData(25544L, 29.0)]
    [InlineData(66052L, 1.0)]  // HRC MONOBLOCK CAMERA, the lowest perigee in the group (about 273 km)
    [InlineData(66052L, 15.0)]
    [InlineData(66052L, 29.0)]
    public void Overhead_peak_shortfall_stays_within_the_stated_uncertainty(long catalogNumber, double daysAfterEpoch)
    {
        var elements = Stations.Single(r => r.Elements.CatalogNumber == catalogNumber).ToSgp4Elements();
        var propagator = Sgp4Propagator.Create(elements);
        var t0 = elements.Epoch.AddDays(daysAfterEpoch);
        var state = propagator.Propagate(t0);
        Assert.True(state.Succeeded, $"SGP4 failed at +{daysAfterEpoch} days: {state.Error}.");

        // The observer at the subpoint at t0, at zero height, sees a 90 degree peak exactly at t0.
        var subpoint = Wgs84.FromEcef(EarthRotation.TemeToEcef(state.State, t0).Position);
        var observer = new TopocentricFrame(subpoint with { HeightKm = 0 });
        var start = t0.AddTicks(-(long)Math.Round(1800.05 * TimeSpan.TicksPerSecond));

        var pass = PassFinder.Find(propagator, observer, start, start.AddHours(1), 10.0).Passes
            .Single(p => Math.Abs((p.Culmination.Time - t0).TotalSeconds) < 1);

        double shortfall = 90.0 - pass.Culmination.ElevationDegrees;
        Assert.InRange(shortfall, 0.0, pass.PeakElevationUncertaintyDegrees);
        Assert.InRange((pass.Culmination.Time - t0).TotalSeconds, -2.1e-4, 2.1e-4);
    }
}
