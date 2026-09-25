using Sky.Orbital.Astronomy;
using Sky.Orbital.Frames;
using Sky.Orbital.Numerics;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Passes;

/// <summary>What starts or ends a visible part of a pass.</summary>
public enum VisibilityChange
{
    /// <summary>The satellite rises above the minimum elevation.</summary>
    Rise,

    /// <summary>The satellite sets below the minimum elevation.</summary>
    Set,

    /// <summary>The satellite leaves the Earth's shadow.</summary>
    LeavesShadow,

    /// <summary>The satellite enters the Earth's shadow.</summary>
    EntersShadow,

    /// <summary>The Sun sinks below −6° at the observer.</summary>
    SkyDarkens,

    /// <summary>The Sun climbs above −6° at the observer.</summary>
    SkyBrightens,
}

/// <summary>A stretch of a pass during which the satellite can be seen.</summary>
/// <param name="Start">When it becomes visible, and where.</param>
/// <param name="StartsBecause">What makes it visible then.</param>
/// <param name="Highest">The highest point while visible.</param>
/// <param name="End">When it stops being visible, and where.</param>
/// <param name="EndsBecause">What ends it.</param>
public sealed record VisibleWindow(PassEvent Start, VisibilityChange StartsBecause, PassEvent Highest, PassEvent End, VisibilityChange EndsBecause);

/// <summary>
/// Which parts of a pass can be seen: the satellite sunlit, and the Sun below −6 degrees at the
/// observer (civil twilight, assumption A12).
/// </summary>
/// <remarks>
/// <para>
/// Inside a pass, Sky samples the shadow function (<see cref="EarthShadow.Function"/>) and the Sun's
/// elevation every 10 s and finds each sign change with Brent's method to 1 ms. A function can cross
/// zero and come back between two samples, so each is given a bound on how fast it can change:
/// </para>
/// <list type="bullet">
/// <item>
/// The shadow function is a distance in the stretched space, so it moves no faster than the
/// satellite does there, plus the turn of the line toward the Sun (mostly the Earth's rotation)
/// times the distance along the line to its closest point. The stretch by 1/(1 − f) enlarges each
/// factor by at most that ratio.
/// </item>
/// <item>
/// The Sun's elevation changes no faster than the Earth turns, 0.00418 degrees per second, plus the
/// Sun's own motion along the ecliptic, 0.00002.
/// </item>
/// </list>
/// <para>
/// Where two samples have the same sign but together are close enough to zero that the bound allows
/// a crossing, the function's extreme between them is found with Brent's minimizer, and if it crosses,
/// both crossings are found. That relies on each function turning at most once within 10 s, which
/// holds by a wide margin: the shadow function's turning points are set by the orbit (the ISS's are
/// tens of minutes apart), and the Sun's elevation turns twice a day.
/// </para>
/// </remarks>
public static class Visibility
{
    /// <summary>The Sun's elevation below which the sky is dark enough, in degrees.</summary>
    public const double TwilightSunElevationDegrees = -6.0;

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    // Rate bounds; see the remarks. Earth rotation 7.2921e-5 rad/s plus the Sun's apparent motion
    // (2e-7 rad/s) plus the change of direction from the satellite's own motion (under 1e-7 rad/s at
    // 1 AU), rounded up.
    private const double LineTurnRadiansPerSecond = 7.35e-5;
    private const double SpeedAllowanceKmPerSecond = 0.1;

    /// <summary>An upper bound on how fast the Sun's elevation changes anywhere on the Earth, degrees per second.</summary>
    internal const double SunElevationRateBoundDegreesPerSecond = 0.0043;
    private const double StretchFactor = 1.0 / (1.0 - Wgs84.Flattening);

    /// <summary>The visible parts of a pass, in time order. Empty if none of it can be seen.</summary>
    public static IReadOnlyList<VisibleWindow> Windows(SatellitePass pass, Sgp4Propagator propagator, TopocentricFrame observer)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(propagator);
        ArgumentNullException.ThrowIfNull(observer);

        DateTimeOffset origin = pass.Rise.Time;
        double length = (pass.Set.Time - origin).TotalSeconds;

        double Shadow(double seconds)
        {
            DateTimeOffset t = At(origin, seconds);
            return EarthShadow.Function(SatelliteEcef(propagator, t).Position, Sun.PositionEcef(t));
        }

        double Dark(double seconds) => TwilightSunElevationDegrees - SunElevationDegrees(observer, At(origin, seconds));

        double ShadowRate(double seconds) => ShadowRateBound(SatelliteEcef(propagator, At(origin, seconds)));

        var changes = new List<(double Seconds, VisibilityChange Change)>();
        foreach (double root in SignChanges(Shadow, ShadowRate, length))
        {
            changes.Add((root, Shadow(Math.Min(root + 0.01, length)) > 0 ? VisibilityChange.LeavesShadow : VisibilityChange.EntersShadow));
        }

        foreach (double root in SignChanges(Dark, _ => SunElevationRateBoundDegreesPerSecond, length))
        {
            changes.Add((root, Dark(Math.Min(root + 0.01, length)) > 0 ? VisibilityChange.SkyDarkens : VisibilityChange.SkyBrightens));
        }

        changes.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        var boundaries = new List<(double Seconds, VisibilityChange Change)> { (0.0, VisibilityChange.Rise) };
        boundaries.AddRange(changes.Where(c => c.Seconds > 0 && c.Seconds < length));
        boundaries.Add((length, VisibilityChange.Set));

        var windows = new List<VisibleWindow>();
        int k = 0;
        while (k < boundaries.Count - 1)
        {
            if (!IsVisible((boundaries[k].Seconds + boundaries[k + 1].Seconds) / 2))
            {
                k++;
                continue;
            }

            // Merge consecutive visible intervals: a boundary can separate two visible ones when, for
            // example, both functions change at nearly the same moment.
            int first = k;
            while (k < boundaries.Count - 1 && IsVisible((boundaries[k].Seconds + boundaries[k + 1].Seconds) / 2))
            {
                k++;
            }

            double from = boundaries[first].Seconds;
            double to = boundaries[k].Seconds;
            windows.Add(new VisibleWindow(
                Event(propagator, observer, At(origin, from)),
                boundaries[first].Change,
                Highest(pass, propagator, observer, At(origin, from), At(origin, to)),
                Event(propagator, observer, At(origin, to)),
                boundaries[k].Change));
        }

        return windows;

        bool IsVisible(double seconds) => Shadow(seconds) > 0 && Dark(seconds) > 0;
    }

    /// <summary>
    /// An upper bound on how fast the shadow function can change over the 10 s after a satellite
    /// state, km/s. In the stretched space the satellite moves at most its speed times k
    /// (k = 1/(1 − f)); the line's direction turns at most k times as fast as the true one; and the
    /// closest point is at most the stretched radius, k·r, along the line from the satellite. Speed
    /// and radius get the allowances a 10 s step can need.
    /// </summary>
    internal static double ShadowRateBound(EcefState state)
    {
        double speed = state.Velocity.Length + SpeedAllowanceKmPerSecond;
        double radius = state.Position.Length + (speed * Step.TotalSeconds);
        return (speed * StretchFactor) + (LineTurnRadiansPerSecond * StretchFactor * radius * StretchFactor);
    }

    /// <summary>The Sun's geometric elevation at the observer, in degrees, including parallax.</summary>
    public static double SunElevationDegrees(TopocentricFrame observer, DateTimeOffset t)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return observer.LookAt(new EcefState(Sun.PositionEcef(t), default)).ElevationDegrees;
    }

    /// <summary>Whether the satellite is in sunlight at an instant.</summary>
    public static bool IsSunlit(Sgp4Propagator propagator, DateTimeOffset t)
    {
        ArgumentNullException.ThrowIfNull(propagator);
        return EarthShadow.IsSunlit(SatelliteEcef(propagator, t).Position, Sun.PositionEcef(t));
    }

    /// <summary>
    /// Every sign change of <paramref name="f"/> on [0, length], using 10 s samples, a bound on the
    /// function's rate, and Brent's methods.
    /// </summary>
    /// <param name="f">The function, of seconds from the start.</param>
    /// <param name="rate">An upper bound on |f'| over the step that follows the given instant.</param>
    /// <param name="length">The interval's length in seconds.</param>
    internal static List<double> SignChanges(Func<double, double> f, Func<double, double> rate, double length)
    {
        var roots = new List<double>();
        int steps = Math.Max(1, (int)Math.Ceiling(length / Step.TotalSeconds));
        double h = length / steps;
        double a = 0.0;
        double fa = f(a);
        for (int i = 1; i <= steps; i++)
        {
            double b = i == steps ? length : i * h;
            double fb = f(b);
            if ((fa > 0) != (fb > 0))
            {
                roots.Add(Brent.FindRoot(f, a, b, PassFinder.TimeToleranceSeconds).X);
            }
            else if (Math.Abs(fa) + Math.Abs(fb) <= rate(a) * (b - a))
            {
                // Both ends on the same side but near enough to zero that the function could cross
                // and come back: find its extreme toward zero.
                double sign = fa > 0 ? 1.0 : -1.0;
                SearchResult extreme = Brent.Minimize(x => sign * f(x), a, b, PassFinder.TimeToleranceSeconds / 10);
                if ((extreme.Value * sign > 0) != (fa > 0))
                {
                    roots.Add(Brent.FindRoot(f, a, extreme.X, PassFinder.TimeToleranceSeconds).X);
                    roots.Add(Brent.FindRoot(f, extreme.X, b, PassFinder.TimeToleranceSeconds).X);
                }
            }

            a = b;
            fa = fb;
        }

        return roots;
    }

    private static PassEvent Highest(SatellitePass pass, Sgp4Propagator propagator, TopocentricFrame observer, DateTimeOffset from, DateTimeOffset to)
    {
        if (pass.Culmination.Time >= from && pass.Culmination.Time <= to)
        {
            return pass.Culmination;
        }

        // Outside the culmination, elevation over a window of an ordinary pass rises or falls
        // throughout, so the higher end wins. A pass with several maxima can have one inside the
        // window; the minimizer finds it if so.
        PassEvent start = Event(propagator, observer, from);
        PassEvent end = Event(propagator, observer, to);
        PassEvent best = start.ElevationDegrees >= end.ElevationDegrees ? start : end;
        double span = (to - from).TotalSeconds;
        if (span > 0)
        {
            SearchResult found = Brent.Minimize(s => -Event(propagator, observer, At(from, s)).ElevationDegrees, 0.0, span, PassFinder.PeakToleranceSeconds);
            PassEvent inside = Event(propagator, observer, At(from, found.X));
            if (inside.ElevationDegrees > best.ElevationDegrees)
            {
                best = inside;
            }
        }

        return best;
    }

    private static PassEvent Event(Sgp4Propagator propagator, TopocentricFrame observer, DateTimeOffset t)
    {
        LookAngles look = observer.LookAt(SatelliteEcef(propagator, t));
        return new PassEvent(t, look.AzimuthDegrees, look.ElevationDegrees);
    }

    private static EcefState SatelliteEcef(Sgp4Propagator propagator, DateTimeOffset t)
    {
        PropagationResult result = propagator.Propagate(t);
        return result.Succeeded
            ? EarthRotation.TemeToEcef(result.State, t)
            : throw new InvalidOperationException($"SGP4 failed at {t:O} inside a pass: {result.Error}.");
    }

    private static DateTimeOffset At(DateTimeOffset origin, double seconds) =>
        origin.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
}
