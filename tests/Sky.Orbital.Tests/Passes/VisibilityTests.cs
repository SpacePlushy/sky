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
