using Sky.Orbital.Elements;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;
using Sky.Orbital.Tests.CrossCheck;

namespace Sky.Orbital.Tests.Passes;

public class CoarsePassFinderTests
{
    private static readonly SkyfieldReference Reference = SkyfieldReference.Instance;
    private static readonly Sgp4Propagator Iss = Sgp4Propagator.Create(Reference.MeanElements);
    private static readonly TopocentricFrame Phoenix = new(Reference.ObserverLocation);
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rise_and_set_are_the_first_and_last_samples_at_or_above_the_minimum()
    {
        // The finder's bookkeeping, checked sample by sample: the step before rise and the step
        // after set are below the minimum; rise, culmination, and set are at or above it.
        var passes = CoarsePassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), 10.0).Passes;

        Assert.NotEmpty(passes);
        foreach (var pass in passes)
        {
            Assert.True(ElevationAt(pass.Rise.Time - CoarsePassFinder.Step) < 10.0);
            Assert.True(pass.Rise.ElevationDegrees >= 10.0);
            Assert.True(pass.Culmination.ElevationDegrees >= pass.Rise.ElevationDegrees);
            Assert.True(pass.Culmination.ElevationDegrees >= pass.Set.ElevationDegrees);
            Assert.True(pass.Set.ElevationDegrees >= 10.0);
            Assert.True(ElevationAt(pass.Set.Time + CoarsePassFinder.Step) < 10.0);
            Assert.True(pass.Rise.Time < pass.Culmination.Time || pass.Rise == pass.Culmination);
            Assert.True(pass.Culmination.Time <= pass.Set.Time);
        }
    }

    [Fact]
    public void A_pass_already_in_progress_at_the_start_is_not_reported()
    {
        // Its rise happened before the window, so the finder cannot report it.
        var first = Reference.Passes[0];
        var start = first.Rise.Utc.AddSeconds(30);

        var passes = CoarsePassFinder.Find(Iss, Phoenix, start, start.AddDays(1), 10.0).Passes;

        Assert.True(passes[0].Rise.Time > first.Set.Utc);
    }

    [Fact]
    public void A_pass_still_in_progress_at_the_end_is_not_reported()
    {
        var second = Reference.Passes[1];
        var end = second.Set.Utc.AddSeconds(-30);

        var passes = CoarsePassFinder.Find(Iss, Phoenix, WindowStart, end, 10.0).Passes;

        Assert.All(passes, p => Assert.True(p.Set.Time < second.Rise.Utc));
    }

    [Fact]
    public void Stops_at_decay_and_says_why()
    {
        // Vallado verification satellite 28872 reports decay about 55 minutes after epoch. The
        // search must stop there and report it, so a decayed satellite does not look like one
        // that simply has no passes.
        var decaying = Tle.Parse(
            "1 28872U 05037B   05333.02012661  .25992681  00000-0  24476-3 0  1534",
            "2 28872  96.4736 157.9986 0303955 244.0492 110.6523 16.46015938 10708");
        var propagator = Sgp4Propagator.Create(decaying);

        var result = CoarsePassFinder.Find(propagator, Phoenix, decaying.Epoch, decaying.Epoch.AddDays(1), 0.0);

        Assert.Equal(Sgp4Error.Decayed, result.StoppedBy);
        Assert.InRange(result.StoppedAt!.Value, decaying.Epoch.AddMinutes(50), decaying.Epoch.AddMinutes(60));
        Assert.All(result.Passes, p => Assert.True(p.Set.Time < result.StoppedAt));
    }

    [Fact]
    public void A_complete_search_reports_no_stop()
    {
        var result = CoarsePassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(1), 10.0);

        Assert.Equal(Sgp4Error.None, result.StoppedBy);
        Assert.Null(result.StoppedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void An_overhead_pass_peaks_at_90_degrees_at_the_moment_the_satellite_is_overhead(int secondsOffGrid)
    {
        // Exact geometry: an observer at the ISS's subpoint at time t0, at zero height, has the ISS on
        // its local vertical at t0, so the true peak is 90 degrees at t0. Elevation changes about 1
        // degree per second near the zenith, the worst case for sampling. The search grid is placed
        // so t0 falls between samples.
        var t0 = WindowStart.AddHours(5);
        var ecef = EarthRotation.TemeToEcef(Iss.Propagate(t0).State, t0);
        var subpoint = Wgs84.FromEcef(ecef.Position);
        var observer = new TopocentricFrame(subpoint with { HeightKm = 0 });
        var start = t0.AddSeconds(-1800 - secondsOffGrid);

        var passes = CoarsePassFinder.Find(Iss, observer, start, start.AddHours(1), 10.0).Passes;

        var pass = Assert.Single(passes);
        // Bounds: the peak search steps 0.1 s, so the best sample is within 0.1 s of the true peak
        // and at most 0.05 s of the nearer bracketing sample. The line of sight turns at most
        // 7.7 km/s / 410 km = 1.08 deg/s, so the shortfall is at most 0.054 deg.
        Assert.InRange((pass.Culmination.Time - t0).TotalSeconds, -0.1, 0.1);
        Assert.InRange(pass.Culmination.ElevationDegrees, 90.0 - 0.06, 90.0);
    }

    private static double ElevationAt(DateTimeOffset t) =>
        Phoenix.LookAt(EarthRotation.TemeToEcef(Iss.Propagate(t).State, t)).ElevationDegrees;
}
