using Sky.Orbital.Elements;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;
using Sky.Orbital.Tests.CrossCheck;

namespace Sky.Orbital.Tests.Passes;

public class PassFinderTests
{
    private static readonly SkyfieldReference Reference = SkyfieldReference.Instance;
    private static readonly Sgp4Propagator Iss = Sgp4Propagator.Create(Reference.MeanElements);
    private static readonly TopocentricFrame Phoenix = new(Reference.ObserverLocation);
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);

    // Elevation at rise and set: the crossing is within 1 ms, where elevation changes by at most
    // the line-of-sight rate (under 1.1 degrees per second for the ISS) times 1 ms.
    private const double CrossingElevationTolerance = 1.1e-3;

    [Fact]
    public void Rise_and_set_are_crossings_of_the_minimum_with_the_pass_above_it_between()
    {
        var passes = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), 10.0).Passes;

        // Skyfield's 25 complete passes, plus one already up at 04:00 that rose a few minutes earlier.
        Assert.Equal(26, passes.Count);
        Assert.True(passes[0].Rise.Time < WindowStart && passes[0].Set.Time > WindowStart);
        foreach (var pass in passes)
        {
            Assert.Equal(10.0, pass.Rise.ElevationDegrees, CrossingElevationTolerance);
            Assert.Equal(10.0, pass.Set.ElevationDegrees, CrossingElevationTolerance);
            Assert.True(ElevationAt(pass.Rise.Time.AddSeconds(-0.002)) < 10.0);
            Assert.True(ElevationAt(pass.Set.Time.AddSeconds(0.002)) < 10.0);
            Assert.True(pass.Rise.Time < pass.Culmination.Time && pass.Culmination.Time < pass.Set.Time);

            // Above the minimum all the way through, sampled every second.
            for (var t = pass.Rise.Time.AddSeconds(0.002); t < pass.Set.Time.AddSeconds(-0.002); t = t.AddSeconds(1))
            {
                Assert.True(ElevationAt(t) >= 10.0 - 1e-9, $"Dips below the minimum at {t:O}.");
                Assert.True(ElevationAt(t) <= pass.Culmination.ElevationDegrees + pass.PeakElevationUncertaintyDegrees);
            }
        }
    }

    [Fact]
    public void Between_passes_the_satellite_stays_below_the_minimum()
    {
        // Sampled every 2 s between each set and the next rise. A missed pass shows up here, since
        // no ISS pass above 10 degrees lasts under 2 s... except a grazing one, which the grazing
        // test covers; here the minimum is a round 10 and every pass clears it by over 0.8 degrees.
        var passes = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(2), 10.0).Passes;
        for (int k = 0; k + 1 < passes.Count; k++)
        {
            for (var t = passes[k].Set.Time.AddSeconds(0.01); t < passes[k + 1].Rise.Time; t = t.AddSeconds(2))
            {
                Assert.True(ElevationAt(t) < 10.0, $"Above the minimum at {t:O}, between passes {k} and {k + 1}.");
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(19)]
    public void A_pass_in_progress_at_the_start_is_reported_with_its_real_rise(int index)
    {
        // Start the search at several points inside a pass. The pass must come back with the same
        // rise, peak, and set as a search that started well before it.
        var full = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), 10.0).Passes;
        var pass = full[index];
        foreach (double fraction in new[] { 0.01, 0.5, 0.99 })
        {
            var start = pass.Rise.Time + ((pass.Set.Time - pass.Rise.Time) * fraction);
            var found = PassFinder.Find(Iss, Phoenix, start, start.AddHours(3), 10.0);

            var first = found.Passes[0];
            Assert.Equal(pass.Rise.Time, first.Rise.Time, TimeSpan.FromMilliseconds(2));
            Assert.Equal(pass.Set.Time, first.Set.Time, TimeSpan.FromMilliseconds(2));
            Assert.Equal(pass.Culmination.ElevationDegrees, first.Culmination.ElevationDegrees, 1e-6);
            Assert.Null(found.AboveMinimumAtStartSince);
        }
    }

    [Fact]
    public void A_pass_still_up_at_the_end_is_reported_with_its_real_set()
    {
        var full = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(1), 10.0).Passes;
        var second = full[1];
        var end = second.Rise.Time.AddSeconds(30);

        var found = PassFinder.Find(Iss, Phoenix, WindowStart, end, 10.0);

        Assert.Equal(2, found.Passes.Count);
        Assert.Equal(second.Set.Time, found.Passes[1].Set.Time, TimeSpan.FromMilliseconds(2));
        Assert.Null(found.AboveMinimumAtEndUntil);
    }

    [Fact]
    public void A_pass_that_ended_just_before_the_start_or_rises_just_after_the_end_is_left_out()
    {
        var full = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(1), 10.0).Passes;
        var start = full[0].Set.Time.AddSeconds(1);
        var end = full[1].Rise.Time.AddSeconds(-1);

        var found = PassFinder.Find(Iss, Phoenix, start, end, 10.0);

        Assert.Empty(found.Passes);
        Assert.Null(found.AboveMinimumAtStartSince);
        Assert.Null(found.AboveMinimumAtEndUntil);
    }

    [Fact]
    public void A_geostationary_satellite_that_never_sets_has_no_passes_and_says_so()
    {
        // Vallado verification case 28626, a geostationary satellite: seen from under it, it stays
        // about 50 degrees up for days, so no rise or set exists within the one-day extension.
        var geo = Tle.Parse(
            "1 28626U 05008A   06176.46683397 -.00000205  00000-0  10000-3 0  2190",
            "2 28626   0.0019 286.9433 0000335  13.7918  55.6504  1.00270176  4891");
        var propagator = Sgp4Propagator.Create(geo);
        var t = geo.Epoch;
        var subpoint = Wgs84.FromEcef(EarthRotation.TemeToEcef(propagator.Propagate(t).State, t).Position);
        var observer = new TopocentricFrame(new Geodetic(40.0, subpoint.LongitudeDegrees, 0));

        var result = PassFinder.Find(propagator, observer, t, t.AddDays(2), 10.0);

        Assert.Empty(result.Passes);
        Assert.NotNull(result.AboveMinimumAtStartSince);
        Assert.NotNull(result.AboveMinimumAtEndUntil);
        Assert.Equal(t - PassFinder.MaximumExtension, result.AboveMinimumAtStartSince!.Value);
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

        var result = PassFinder.Find(propagator, Phoenix, decaying.Epoch, decaying.Epoch.AddDays(1), 0.0);

        Assert.Equal(Sgp4Error.Decayed, result.StoppedBy);
        Assert.InRange(result.StoppedAt!.Value, decaying.Epoch.AddMinutes(50), decaying.Epoch.AddMinutes(60));
        Assert.All(result.Passes, p => Assert.True(p.Set.Time < result.StoppedAt));
    }

    [Fact]
    public void A_complete_search_reports_no_stop()
    {
        var result = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(1), 10.0);

        Assert.Equal(Sgp4Error.None, result.StoppedBy);
        Assert.Null(result.StoppedAt);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(3.15)]
    [InlineData(7.05)]
    [InlineData(0.10)]
    [InlineData(5.00)]
    [InlineData(9.99)]
    public void An_overhead_pass_peaks_at_90_degrees_at_the_moment_the_satellite_is_overhead(double secondsOffGrid)
    {
        // Exact geometry: an observer at the ISS's subpoint at time t0, at zero height, has the ISS
        // on its local vertical at t0, so the true peak is 90 degrees at t0. Near the zenith
        // elevation has a corner, about 1 degree per second either side: the hardest peak for a
        // minimizer, and the case the stated uncertainty is sized for. The grid phase varies so the
        // peak falls at different places between the 10 s samples.
        var t0 = WindowStart.AddHours(5);
        var ecef = EarthRotation.TemeToEcef(Iss.Propagate(t0).State, t0);
        var subpoint = Wgs84.FromEcef(ecef.Position);
        var observer = new TopocentricFrame(subpoint with { HeightKm = 0 });
        var start = t0.AddTicks(-(long)Math.Round((1800 + secondsOffGrid) * TimeSpan.TicksPerSecond));

        var pass = Assert.Single(PassFinder.Find(Iss, observer, start, start.AddHours(1), 10.0).Passes);

        // Brent places the peak within 2 (1e-4 + 1.5e-7) s; the elevation there is low by at most
        // the line-of-sight rate times that, which the stated uncertainty bounds.
        double shortfall = 90.0 - pass.Culmination.ElevationDegrees;
        Assert.InRange((pass.Culmination.Time - t0).TotalSeconds, -2.1e-4, 2.1e-4);
        Assert.InRange(shortfall, 0.0, pass.PeakElevationUncertaintyDegrees);
        Assert.True(pass.PeakElevationUncertaintyDegrees < 3e-4, $"Uncertainty {pass.PeakElevationUncertaintyDegrees} is larger than the analysis allows.");
    }

    [Fact]
    public void Grazing_passes_are_found_with_rise_and_set_at_the_minimum()
    {
        // Set the minimum just below each ISS pass's true peak (0.02 to 0.2 degrees). Often no 10 s
        // sample clears it. Every pass must still be found, with rise and set on the minimum either
        // side of the peak.
        var reference = SkyfieldReference.Instance.Passes;
        foreach (var (expected, index) in reference.Select((p, i) => (p, i)))
        {
            foreach (double margin in new[] { 0.02, 0.05, 0.2 })
            {
                double minimum = expected.Culmination.ElevationDeg - margin;
                var match = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), minimum).Passes
                    .Where(p => Math.Abs((p.Culmination.Time - expected.Culmination.Utc).TotalSeconds) < 60)
                    .ToList();

                var pass = Assert.Single(match);
                Assert.True(pass.Rise.Time < pass.Culmination.Time, $"Pass {index}, margin {margin}: rise not before peak.");
                Assert.True(pass.Culmination.Time < pass.Set.Time, $"Pass {index}, margin {margin}: set not after peak.");
                Assert.Equal(minimum, pass.Rise.ElevationDegrees, CrossingElevationTolerance);
                Assert.Equal(minimum, pass.Set.ElevationDegrees, CrossingElevationTolerance);
                Assert.InRange((pass.Culmination.Time - expected.Culmination.Utc).TotalSeconds, -0.01, 0.01);
            }
        }
    }

    [Fact]
    public void A_pass_with_two_elevation_maxima_reports_the_higher_one()
    {
        // Vallado's Molniya case (12 h, e = 0.69) seen from 45 N, 100 W: a 10.9 hour pass with
        // maxima of about 79.4 and 85.1 degrees, 7.7 hours apart. The reference is a brute-force
        // 0.1 s sweep of the whole pass, then golden-section refinement of its best sample.
        var molniya = Tle.Parse(
            "1 08195U 75081A   06176.33215444  .00000099  00000-0  11873-3 0   813",
            "2 08195  64.1586 279.0717 6877146 264.7651  20.2257  2.00491383225656");
        var propagator = Sgp4Propagator.Create(molniya);
        var observer = new TopocentricFrame(new Geodetic(45.0, -100.0, 0.0));
        var start = new DateTimeOffset(2006, 6, 26, 7, 0, 0, TimeSpan.Zero);

        var pass = Assert.Single(PassFinder.Find(propagator, observer, start, start.AddHours(13), 10.0).Passes);

        DateTimeOffset bestTime = pass.Rise.Time;
        double best = double.MinValue;
        for (var t = pass.Rise.Time; t <= pass.Set.Time; t = t.AddTicks(1_000_000))
        {
            double elevation = observer.LookAt(EarthRotation.TemeToEcef(propagator.Propagate(t).State, t)).ElevationDegrees;
            if (elevation > best)
            {
                best = elevation;
                bestTime = t;
            }
        }

        Assert.True(best > 85.0, $"Brute force found {best} degrees; the test pass is not the expected one.");

        // The brute force's best 0.1 s sample can only be at or below the true peak.
        Assert.True(pass.Culmination.ElevationDegrees >= best - 1e-9, $"Reported {pass.Culmination.ElevationDegrees}, a 0.1 s sample reached {best}.");
        Assert.InRange((pass.Culmination.Time - bestTime).TotalSeconds, -0.101, 0.101);
        Assert.Equal(10.0, pass.Rise.ElevationDegrees, 1e-4);
        Assert.Equal(10.0, pass.Set.ElevationDegrees, 1e-4);
    }

    [Fact]
    public void A_dip_below_the_minimum_between_two_samples_splits_the_pass()
    {
        // The Molniya pass above has maxima of 79.4 and 85.1 degrees with a minimum between them.
        // Put the minimum elevation just above that lowest point, so elevation dips below it for about
        // 6 s, centered between two 10 s samples. Every sample stays above the minimum, so only the
        // rate-bound check can find the dip, and the pass must come back as two.
        var molniya = Tle.Parse(
            "1 08195U 75081A   06176.33215444  .00000099  00000-0  11873-3 0   813",
            "2 08195  64.1586 279.0717 6877146 264.7651  20.2257  2.00491383225656");
        var propagator = Sgp4Propagator.Create(molniya);
        var observer = new TopocentricFrame(new Geodetic(45.0, -100.0, 0.0));
        var start = new DateTimeOffset(2006, 6, 26, 7, 0, 0, TimeSpan.Zero);
        var whole = Assert.Single(PassFinder.Find(propagator, observer, start, start.AddHours(13), 10.0).Passes);

        double Elevation(DateTimeOffset t) => observer.LookAt(EarthRotation.TemeToEcef(propagator.Propagate(t).State, t)).ElevationDegrees;

        // The lowest point between the maxima: a coarse sweep, then Brent's minimizer.
        var coarse = Enumerable.Range(0, 400).Select(k => whole.Rise.Time.AddHours(1).AddMinutes(k)).Where(t => t < whole.Set.Time.AddHours(-1)).MinBy(Elevation);
        var lowest = Sky.Orbital.Numerics.Brent.Minimize(s => Elevation(coarse.AddSeconds(s)), -60, 60, 1e-3);
        var bottom = coarse.AddTicks((long)Math.Round(lowest.X * TimeSpan.TicksPerSecond));
        double curvature = (Elevation(bottom.AddSeconds(60)) + Elevation(bottom.AddSeconds(-60)) - (2 * lowest.Value)) / 3600.0; // deg/s²
        Assert.True(curvature > 0, "No minimum between the maxima.");
        double minimum = lowest.Value + (0.5 * curvature * 3.0 * 3.0); // about 3 s either side of the bottom

        // Samples fall at bottom ± 5 s, ± 15 s, ...: none inside the dip.
        var searchStart = bottom.AddSeconds(-5).AddSeconds(-10 * 360);
        var passes = PassFinder.Find(propagator, observer, searchStart, searchStart.AddHours(2), minimum).Passes;
        Assert.True(Elevation(bottom.AddSeconds(-5)) > minimum && Elevation(bottom.AddSeconds(5)) > minimum);

        var near = passes.Where(p => Math.Abs((p.Set.Time - bottom).TotalMinutes) < 1 || Math.Abs((p.Rise.Time - bottom).TotalMinutes) < 1).ToList();
        Assert.Equal(2, near.Count);
        Assert.InRange((bottom - near[0].Set.Time).TotalSeconds, 1.0, 5.0);
        Assert.InRange((near[1].Rise.Time - bottom).TotalSeconds, 1.0, 5.0);
        Assert.True(near[0].Set.Time < near[1].Rise.Time);
        Assert.Equal(minimum, Elevation(near[0].Set.Time), 1e-6);
        Assert.Equal(minimum, Elevation(near[1].Rise.Time), 1e-6);
    }

    [Fact]
    public void Random_observers_give_ordered_non_overlapping_passes_with_crossings_on_the_minimum()
    {
        // Invariants of a correct finder, for seeded random observers and minimum elevations.
        var random = new Random(42);
        for (int i = 0; i < 40; i++)
        {
            var observer = new TopocentricFrame(new Geodetic(
                (random.NextDouble() * 120) - 60,
                (random.NextDouble() * 360) - 180,
                random.NextDouble() * 3));
            double minimum = random.NextDouble() * 40;
            var start = WindowStart.AddSeconds(random.NextDouble() * 86400);

            var passes = PassFinder.Find(Iss, observer, start, start.AddDays(2), minimum).Passes;

            for (int k = 0; k < passes.Count; k++)
            {
                var p = passes[k];
                Assert.True(p.Rise.Time < p.Culmination.Time && p.Culmination.Time < p.Set.Time);
                Assert.Equal(minimum, p.Rise.ElevationDegrees, CrossingElevationTolerance);
                Assert.Equal(minimum, p.Set.ElevationDegrees, CrossingElevationTolerance);
                Assert.True(p.Culmination.ElevationDegrees >= minimum);
                Assert.True(p.Set.Time >= start && p.Rise.Time <= start.AddDays(2));
                if (k > 0)
                {
                    Assert.True(passes[k - 1].Set.Time < p.Rise.Time);
                }

                // The observer's own elevation function, evaluated independently at the reported times.
                var observed = observer.LookAt(EarthRotation.TemeToEcef(Iss.Propagate(p.Culmination.Time).State, p.Culmination.Time));
                Assert.Equal(observed.ElevationDegrees, p.Culmination.ElevationDegrees, 1e-12);
            }
        }
    }

    private static double ElevationAt(DateTimeOffset t) =>
        Phoenix.LookAt(EarthRotation.TemeToEcef(Iss.Propagate(t).State, t)).ElevationDegrees;
}
