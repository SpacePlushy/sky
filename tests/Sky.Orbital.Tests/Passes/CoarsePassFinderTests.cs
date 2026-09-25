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
            Assert.True(pass.Rise.Time <= pass.Culmination.Time);
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
    // Phases .05 and .15 put t0 midway between 0.1 s peak samples: the worst case for the refinement.
    // Phases .10 and .20 put t0 on a 0.1 s sample but midway on a 0.2 s grid, so a coarser peak
    // step fails here even though the stated bound has margin.
    [InlineData(0.05, true)]
    [InlineData(3.15, true)]
    [InlineData(7.05, true)]
    [InlineData(0.10, false)]
    [InlineData(3.20, false)]
    [InlineData(5.00, false)]
    public void An_overhead_pass_peaks_at_90_degrees_at_the_moment_the_satellite_is_overhead(double secondsOffGrid, bool midway)
    {
        // Exact geometry: an observer at the ISS's subpoint at time t0, at zero height, has the ISS on
        // its local vertical at t0, so the true peak is 90 degrees at t0. Elevation changes about 1
        // degree per second near the zenith, the worst case for sampling.
        var t0 = WindowStart.AddHours(5);
        var ecef = EarthRotation.TemeToEcef(Iss.Propagate(t0).State, t0);
        var subpoint = Wgs84.FromEcef(ecef.Position);
        var observer = new TopocentricFrame(subpoint with { HeightKm = 0 });
        var start = t0.AddTicks(-(long)Math.Round((1800 + secondsOffGrid) * TimeSpan.TicksPerSecond));

        var pass = Assert.Single(CoarsePassFinder.Find(Iss, observer, start, start.AddHours(1), 10.0).Passes);

        double shortfall = 90.0 - pass.Culmination.ElevationDegrees;
        if (midway)
        {
            // The reported peak is 0.05 s from t0, and the stated uncertainty must cover the shortfall.
            // Near the zenith elevation falls linearly, so the shortfall is the line-of-sight rate times
            // 0.05 s, and the bound (the same rate with its worst change over 0.1 s) is within 1% of it.
            Assert.Equal(0.05, Math.Abs((pass.Culmination.Time - t0).TotalSeconds), 1e-6);
            Assert.InRange(shortfall, 0.02, pass.PeakElevationUncertaintyDegrees);
            Assert.True(pass.PeakElevationUncertaintyDegrees <= 1.01 * shortfall, $"Bound {pass.PeakElevationUncertaintyDegrees} is loose against {shortfall}.");
        }
        else
        {
            // t0 is on a 0.1 s sample, so the peak is found exactly.
            Assert.Equal(0.0, (pass.Culmination.Time - t0).TotalSeconds, 1e-6);
            Assert.True(shortfall < 1e-6, $"Shortfall {shortfall} degrees at an on-grid peak.");
        }
    }

    [Fact]
    public void Grazing_passes_are_found_and_keep_rise_peak_set_in_order()
    {
        // Set the minimum just below each ISS pass's true peak (0.02 to 0.2 degrees). Often no 10 s
        // sample clears it, or only one does and the true peak lies before or after it. Every pass must
        // still be found, with rise at or before the peak and set at or after it.
        var reference = SkyfieldReference.Instance.Passes;
        foreach (var (expected, index) in reference.Select((p, i) => (p, i)))
        {
            foreach (double margin in new[] { 0.02, 0.05, 0.2 })
            {
                double minimum = expected.Culmination.ElevationDeg - margin;
                var match = CoarsePassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), minimum).Passes
                    .Where(p => Math.Abs((p.Culmination.Time - expected.Culmination.Utc).TotalSeconds) < 60)
                    .ToList();

                var pass = Assert.Single(match);
                Assert.True(pass.Rise.Time <= pass.Culmination.Time, $"Pass {index}, margin {margin}: rise after peak.");
                Assert.True(pass.Culmination.Time <= pass.Set.Time, $"Pass {index}, margin {margin}: set before peak.");
                Assert.True(pass.Culmination.ElevationDegrees >= minimum, $"Pass {index}, margin {margin}: peak below minimum.");
                Assert.InRange((pass.Culmination.Time - expected.Culmination.Utc).TotalSeconds, -0.101, 0.101);
            }
        }
    }

    [Fact]
    public void A_pass_with_two_elevation_maxima_reports_the_higher_one()
    {
        // Vallado's Molniya case (12 h, e = 0.69) seen from 45 N, 100 W: a 10.9 hour pass with maxima
        // of about 79.4 and 85.1 degrees, 7.7 hours apart. The reference is a brute-force 0.1 s sweep
        // of the whole pass, which cannot miss a maximum.
        var molniya = Tle.Parse(
            "1 08195U 75081A   06176.33215444  .00000099  00000-0  11873-3 0   813",
            "2 08195  64.1586 279.0717 6877146 264.7651  20.2257  2.00491383225656");
        var propagator = Sgp4Propagator.Create(molniya);
        var observer = new TopocentricFrame(new Geodetic(45.0, -100.0, 0.0));
        var start = new DateTimeOffset(2006, 6, 26, 7, 0, 0, TimeSpan.Zero);

        var pass = Assert.Single(CoarsePassFinder.Find(propagator, observer, start, start.AddHours(13), 10.0).Passes);

        DateTimeOffset bestTime = pass.Rise.Time;
        double best = double.MinValue;
        for (var t = pass.Rise.Time.AddSeconds(-10); t <= pass.Set.Time.AddSeconds(10); t = t.AddTicks(1_000_000))
        {
            double elevation = observer.LookAt(EarthRotation.TemeToEcef(propagator.Propagate(t).State, t)).ElevationDegrees;
            if (elevation > best)
            {
                best = elevation;
                bestTime = t;
            }
        }

        Assert.True(best > 85.0, $"Brute force found {best} degrees; the test pass is not the expected one.");
        Assert.InRange(best - pass.Culmination.ElevationDegrees, -1e-9, pass.PeakElevationUncertaintyDegrees);
        Assert.InRange((pass.Culmination.Time - bestTime).TotalSeconds, -0.101, 0.101);
    }

    private static double ElevationAt(DateTimeOffset t) =>
        Phoenix.LookAt(EarthRotation.TemeToEcef(Iss.Propagate(t).State, t)).ElevationDegrees;
}
