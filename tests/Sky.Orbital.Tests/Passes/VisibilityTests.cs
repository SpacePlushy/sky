using Sky.Orbital.Astronomy;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;
using Sky.Orbital.Tests.CrossCheck;

namespace Sky.Orbital.Tests.Passes;

public class VisibilityTests
{
    private static readonly SkyfieldReference Reference = SkyfieldReference.Instance;
    private static readonly Sgp4Propagator Iss = Sgp4Propagator.Create(Reference.MeanElements);
    private static readonly TopocentricFrame Phoenix = new(Reference.ObserverLocation);
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sign_changes_include_a_crossing_and_return_hidden_between_two_samples()
    {
        // (t − 5.3)² − 0.5 is positive at both samples 0 and 10 but dips below zero between them,
        // crossing at 5.3 ± √0.5. |f'| ≤ 2·|t − 5.3| ≤ 10.6 on [0, 10].
        var roots = Visibility.SignChanges(t => ((t - 5.3) * (t - 5.3)) - 0.5, _ => 10.6, 10.0);

        Assert.Equal(2, roots.Count);
        Assert.Equal(5.3 - Math.Sqrt(0.5), roots[0], 1e-3);
        Assert.Equal(5.3 + Math.Sqrt(0.5), roots[1], 1e-3);
    }

    [Fact]
    public void Sign_changes_ignore_a_dip_that_stays_above_zero()
    {
        var roots = Visibility.SignChanges(t => ((t - 5.3) * (t - 5.3)) + 0.001, _ => 10.6, 10.0);
        Assert.Empty(roots);
    }

    [Fact]
    public void Sign_changes_find_every_root_of_a_slow_sine_over_many_steps()
    {
        // sin((t + 0.5) / 5) on [0, 200]: roots at 5kπ − 0.5, turning points 15.7 s apart, so at
        // most one per 10 s step as the method requires. |f'| ≤ 0.2.
        var roots = Visibility.SignChanges(t => Math.Sin((t + 0.5) / 5), _ => 0.2, 200.0);

        var expected = Enumerable.Range(1, 12).Select(k => (5 * k * Math.PI) - 0.5).ToList();
        Assert.Equal(expected.Count, roots.Count);
        foreach (var (found, exact) in roots.Zip(expected))
        {
            Assert.Equal(exact, found, 1e-3);
        }
    }

    [Fact]
    public void Windows_are_exactly_where_the_satellite_is_sunlit_and_the_sky_is_dark()
    {
        // Over 7 days of ISS passes over Phoenix, check every second of every pass against the Sun
        // and shadow functions directly: inside a window both conditions hold, outside neither-both.
        // Boundaries are within 1 ms, so seconds within 2 ms of a boundary are skipped.
        var passes = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), 10.0).Passes;
        int visiblePasses = 0;
        int visibleSeconds = 0;
        foreach (var pass in passes)
        {
            var windows = Visibility.Windows(pass, Iss, Phoenix);
            visiblePasses += windows.Count > 0 ? 1 : 0;
            foreach (var w in windows)
            {
                Assert.True(w.Start.Time >= pass.Rise.Time && w.End.Time <= pass.Set.Time);
                Assert.True(w.Start.Time <= w.Highest.Time && w.Highest.Time <= w.End.Time);
            }

            for (var t = pass.Rise.Time; t <= pass.Set.Time; t = t.AddSeconds(1))
            {
                if (windows.Any(w => Near(t, w.Start.Time) || Near(t, w.End.Time)))
                {
                    continue;
                }

                bool inside = windows.Any(w => t >= w.Start.Time && t <= w.End.Time);
                bool visible = Visibility.IsSunlit(Iss, t) && Visibility.SunElevationDegrees(Phoenix, t) < -6.0;
                Assert.Equal(visible, inside);
                visibleSeconds += inside ? 1 : 0;
            }
        }

        // The week must contain both kinds of pass for the test to mean anything.
        Assert.InRange(visiblePasses, 1, passes.Count - 1);
        Assert.True(visibleSeconds > 60, $"Only {visibleSeconds} visible seconds in the week.");

        static bool Near(DateTimeOffset t, DateTimeOffset boundary) => Math.Abs((t - boundary).TotalSeconds) < 0.002;
    }

    [Fact]
    public void Each_boundary_is_where_its_cause_changes()
    {
        var passes = PassFinder.Find(Iss, Phoenix, WindowStart, WindowStart.AddDays(7), 10.0).Passes;
        foreach (var pass in passes)
        {
            foreach (var w in Visibility.Windows(pass, Iss, Phoenix))
            {
                CheckBoundary(w.Start, w.StartsBecause, pass);
                CheckBoundary(w.End, w.EndsBecause, pass);
                Assert.True(w.StartsBecause is VisibilityChange.Rise or VisibilityChange.LeavesShadow or VisibilityChange.SkyDarkens);
                Assert.True(w.EndsBecause is VisibilityChange.Set or VisibilityChange.EntersShadow or VisibilityChange.SkyBrightens);
            }
        }
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("molniya")]
    [InlineData("geo")]
    public void The_shadow_rate_bound_holds_over_every_10_second_step(string orbit)
    {
        // The bound decides where a hidden crossing between samples is possible, so it must hold:
        // over each 10 s step, the shadow function's actual rate (central differences, every
        // second) must not exceed the bound computed from the state at the step's start.
        var (propagator, start, hours) = orbit switch
        {
            "iss" => (Iss, WindowStart, 24.0),
            "molniya" => (Sgp4Propagator.Create(Sky.Orbital.Elements.Tle.Parse(
                "1 08195U 75081A   06176.33215444  .00000099  00000-0  11873-3 0   813",
                "2 08195  64.1586 279.0717 6877146 264.7651  20.2257  2.00491383225656")), new DateTimeOffset(2006, 6, 25, 8, 0, 0, TimeSpan.Zero), 24.0),
            _ => (Sgp4Propagator.Create(Sky.Orbital.Elements.Tle.Parse(
                "1 28626U 05008A   06176.46683397 -.00000205  00000-0  10000-3 0  2190",
                "2 28626   0.0019 286.9433 0000335  13.7918  55.6504  1.00270176  4891")), new DateTimeOffset(2006, 6, 25, 12, 0, 0, TimeSpan.Zero), 24.0),
        };

        double Shadow(DateTimeOffset t) => EarthShadow.Function(Ecef(propagator, t).Position, Sun.PositionEcef(t));
        double worst = 0;
        for (var stepStart = start; stepStart < start.AddHours(hours); stepStart = stepStart.AddSeconds(10))
        {
            double bound = Visibility.ShadowRateBound(Ecef(propagator, stepStart));
            for (int k = 0; k <= 10; k++)
            {
                var t = stepStart.AddSeconds(k);
                double rate = Math.Abs(Shadow(t.AddSeconds(0.5)) - Shadow(t.AddSeconds(-0.5)));
                worst = Math.Max(worst, rate / bound);
                Assert.True(rate <= bound, $"{orbit} at {t:O}: {rate} km/s against a bound of {bound}.");
            }
        }

        // The bound is not vacuous: the ISS's actual rate comes within a factor of 2 of it.
        if (orbit == "iss")
        {
            Assert.True(worst > 0.5, $"Worst ratio {worst}.");
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(33.4478)]
    [InlineData(66.0)]
    [InlineData(-89.0)]
    public void The_sun_elevation_rate_bound_holds_everywhere_through_the_year(double latitude)
    {
        var observer = new TopocentricFrame(new Geodetic(latitude, -112.0972, 0.0));
        for (var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); t.Year == 2026; t = t.AddMinutes(37))
        {
            double rate = Math.Abs(Visibility.SunElevationDegrees(observer, t.AddSeconds(5)) - Visibility.SunElevationDegrees(observer, t.AddSeconds(-5))) / 10.0;
            Assert.True(rate <= Visibility.SunElevationRateBoundDegreesPerSecond, $"{latitude}° at {t:O}: {rate} deg/s.");
        }
    }

    [Fact]
    public void Dawn_windows_start_when_the_satellite_leaves_shadow_and_end_when_the_sky_brightens()
    {
        // The week's evening passes never reach these two causes, so build 90-minute stretches
        // across dawn in Phoenix (civil twilight begins about 12:53 UTC), where the ISS comes out of
        // the Earth's shadow into a still-dark sky.
        var starts = new List<VisibilityChange>();
        var ends = new List<VisibilityChange>();
        foreach (int day in new[] { 24, 25, 26, 27, 28, 29, 30 })
        {
            var t0 = new DateTimeOffset(2026, 9, day, 11, 40, 0, TimeSpan.Zero);
            var span = new SatellitePass(new PassEvent(t0, 0, 0), new PassEvent(t0.AddMinutes(45), 0, 0), new PassEvent(t0.AddMinutes(90), 0, 0), 0);
            foreach (var w in Visibility.Windows(span, Iss, Phoenix))
            {
                CheckBoundary(w.Start, w.StartsBecause, span);
                CheckBoundary(w.End, w.EndsBecause, span);
                starts.Add(w.StartsBecause);
                ends.Add(w.EndsBecause);
            }
        }

        Assert.Contains(VisibilityChange.LeavesShadow, starts);
        Assert.Contains(VisibilityChange.SkyBrightens, ends);
    }

    private static EcefState Ecef(Sgp4Propagator propagator, DateTimeOffset t) => EarthRotation.TemeToEcef(propagator.Propagate(t).State, t);

    [Fact]
    public void Sun_elevation_is_the_look_angle_of_the_suns_earth_fixed_position()
    {
        // The Sun as a very distant "satellite": its elevation from the observer's frame, and a
        // direct dot product with the local vertical, must agree.
        var t = WindowStart.AddHours(3);
        Vec3 sun = Sun.PositionEcef(t);
        Vec3 observer = Wgs84.ToEcef(Reference.ObserverLocation);
        Vec3 line = sun - observer;
        double lat = Reference.ObserverLocation.LatitudeDegrees * Math.PI / 180;
        double lon = Reference.ObserverLocation.LongitudeDegrees * Math.PI / 180;
        var up = new Vec3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        double elevation = Math.Asin(line.Dot(up) / line.Length) * 180 / Math.PI;

        Assert.Equal(elevation, Visibility.SunElevationDegrees(Phoenix, t), 1e-9);
    }

    private static void CheckBoundary(PassEvent at, VisibilityChange cause, SatellitePass pass)
    {
        // Within 1 ms of the boundary the function that caused it changes sign.
        var before = at.Time.AddSeconds(-0.002);
        var after = at.Time.AddSeconds(0.002);
        switch (cause)
        {
            case VisibilityChange.Rise:
                Assert.Equal(pass.Rise.Time, at.Time);
                break;
            case VisibilityChange.Set:
                Assert.Equal(pass.Set.Time, at.Time);
                break;
            case VisibilityChange.LeavesShadow:
                Assert.False(Visibility.IsSunlit(Iss, before));
                Assert.True(Visibility.IsSunlit(Iss, after));
                break;
            case VisibilityChange.EntersShadow:
                Assert.True(Visibility.IsSunlit(Iss, before));
                Assert.False(Visibility.IsSunlit(Iss, after));
                break;
            case VisibilityChange.SkyDarkens:
                Assert.True(Visibility.SunElevationDegrees(Phoenix, before) > -6.0);
                Assert.True(Visibility.SunElevationDegrees(Phoenix, after) < -6.0);
                break;
            case VisibilityChange.SkyBrightens:
                Assert.True(Visibility.SunElevationDegrees(Phoenix, before) < -6.0);
                Assert.True(Visibility.SunElevationDegrees(Phoenix, after) > -6.0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause));
        }
    }
}
