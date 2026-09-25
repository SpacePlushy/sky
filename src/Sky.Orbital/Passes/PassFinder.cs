using Sky.Orbital.Frames;
using Sky.Orbital.Numerics;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Passes;

/// <summary>
/// Finds passes by sampling elevation every 10 seconds, then refines rise and set with Brent's
/// root finder and each peak with Brent's minimizer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Finding every pass.</b> The 10 s scan and its safeguards are Milestone 1's. A pass shows up as
/// a run of samples at or above the minimum. A grazing pass can clear the minimum between two
/// samples without any sample doing so, so every local maximum below the minimum that could reach
/// it is refined too: between samples, elevation can gain at most the line-of-sight rate, bounded
/// by the relative speed (plus an allowance for its change) over the closest range it can reach,
/// times 10 s.
/// </para>
/// <para>
/// <b>Rise and set.</b> Each is bracketed by a sample below the minimum and one at or above it (or
/// the refined peak, for a grazing pass), and Brent's method finds the crossing to within
/// <see cref="TimeToleranceSeconds"/>. A crossing inside one 10 s step is taken to be unique: a
/// second and third crossing in the same step would need elevation to reverse twice within 10 s.
/// </para>
/// <para>
/// <b>Peak.</b> Every local maximum among the samples of a pass is refined by Brent's minimizer across
/// one step either side, and the highest result is the culmination. Elevation has no two maxima
/// within 20 s of each other for any orbit, so each true maximum lies inside one of those searches.
/// The minimizer places the peak within <see cref="PeakToleranceSeconds"/> twice over of the true
/// one. Near the zenith elevation has a corner, not a smooth top, so the reported elevation can be
/// low by up to the line-of-sight rate times that distance:
/// <see cref="SatellitePass.PeakElevationUncertaintyDegrees"/>, about 0.0002 degrees for an overhead
/// ISS pass and far less for any other.
/// </para>
/// <para>
/// <b>Passes in progress.</b> A pass already above the minimum when the search starts is followed
/// backward in 10 s steps, for up to <see cref="MaximumExtension"/>, to find its real rise; one
/// still up at the end is followed forward the same way. Every pass that is up at any moment of the
/// search window is reported. A satellite that stays up for the whole extension, such as a
/// geostationary one, has no rise or set to report; the result says so through
/// <see cref="PassSearchResult.AboveMinimumAtStartSince"/> and
/// <see cref="PassSearchResult.AboveMinimumAtEndUntil"/>.
/// </para>
/// <para>
/// If SGP4 fails, for example because the satellite has decayed, sampling stops there and the result
/// says when and why. Elevation is geometric, with no refraction, and UTC is treated as UT1
/// (assumption A5).
/// </para>
/// </remarks>
public static class PassFinder
{
    /// <summary>The sampling step.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    /// <summary>How far before the start and after the end a pass in progress is followed.</summary>
    public static readonly TimeSpan MaximumExtension = TimeSpan.FromDays(1);

    /// <summary>Rise and set lie within this many seconds of the true crossing.</summary>
    public const double TimeToleranceSeconds = 1e-3;

    /// <summary>The absolute tolerance given to Brent's minimizer for each peak, in seconds.</summary>
    public const double PeakToleranceSeconds = 1e-4;

    // Upper bound on how much the relative speed can change in 10 s, km/s. Gravity changes a low
    // orbit's speed by under 0.01 km/s per second; 0.1 km/s over 10 s is ample.
    private const double SpeedAllowanceKmPerSecond = 0.1;

    // Samples beyond each end of the window: the outermost sample must be below the minimum to
    // bracket a crossing, and a local maximum needs a neighbor on each side.
    private const int GuardSamples = 2;

    /// <summary>Finds every pass that is above the minimum elevation at some moment between two instants.</summary>
    /// <param name="propagator">The satellite.</param>
    /// <param name="observer">The observer's local frame.</param>
    /// <param name="start">Start of the search window.</param>
    /// <param name="end">End of the search window, not before <paramref name="start"/>.</param>
    /// <param name="minimumElevationDegrees">Elevation a pass must reach, in degrees.</param>
    public static PassSearchResult Find(
        Sgp4Propagator propagator,
        TopocentricFrame observer,
        DateTimeOffset start,
        DateTimeOffset end,
        double minimumElevationDegrees)
    {
        ArgumentNullException.ThrowIfNull(propagator);
        ArgumentNullException.ThrowIfNull(observer);
        if (end < start)
        {
            throw new ArgumentException("The search must not end before it starts.", nameof(end));
        }

        double minimum = minimumElevationDegrees;
        long maxExtensionSteps = MaximumExtension.Ticks / Step.Ticks;

        // Backward from the start: the guard samples, then on while the satellite is still up.
        var samples = Extend(propagator, observer, start, -1, minimum, maxExtensionSteps);
        samples.Reverse();

        // Through the window on the grid start + k·step.
        Sgp4Error stoppedBy = Sgp4Error.None;
        DateTimeOffset? stoppedAt = null;
        DateTimeOffset lastInWindow = start;
        for (DateTimeOffset t = start; t <= end; t += Step)
        {
            Sample? sample = Observe(propagator, observer, t);
            if (sample is null)
            {
                stoppedBy = propagator.Propagate(t).Error;
                stoppedAt = t;
                break;
            }

            samples.Add(sample.Value);
            lastInWindow = t;
        }

        // Forward past the end the same way, unless SGP4 already stopped the search.
        if (stoppedAt is null)
        {
            samples.AddRange(Extend(propagator, observer, lastInWindow, +1, minimum, maxExtensionSteps));
        }

        var passes = new List<SatellitePass>();
        int i = 0;
        while (i < samples.Count)
        {
            if (samples[i].Event.ElevationDegrees >= minimum)
            {
                int first = i;
                while (i < samples.Count && samples[i].Event.ElevationDegrees >= minimum)
                {
                    i++;
                }

                int last = i - 1;
                if (first == 0 || i == samples.Count)
                {
                    continue; // no sample below the minimum on one side: the rise or set is out of reach
                }

                Sample peak = HighestRefinedPeak(propagator, observer, samples, first - 1, last + 1);
                DateTimeOffset riseBracketEnd = peak.Event.Time < samples[first].Event.Time ? peak.Event.Time : samples[first].Event.Time;
                DateTimeOffset setBracketStart = peak.Event.Time > samples[last].Event.Time ? peak.Event.Time : samples[last].Event.Time;
                PassEvent rise = Crossing(propagator, observer, samples[first - 1].Event.Time, riseBracketEnd, minimum);
                PassEvent set = Crossing(propagator, observer, setBracketStart, samples[last + 1].Event.Time, minimum);
                passes.Add(new SatellitePass(rise, peak.Event, set, PeakUncertaintyDegrees(peak)));
            }
            else
            {
                if (i > 0 && i < samples.Count - 1 && IsLocalMaximum(samples, i)
                    && samples[i].Event.ElevationDegrees + MaximumGainDegrees(samples[i], Step) >= minimum)
                {
                    Sample peak = Refine(propagator, observer, samples[i]);
                    if (peak.Event.ElevationDegrees >= minimum)
                    {
                        PassEvent rise = Crossing(propagator, observer, samples[i - 1].Event.Time, peak.Event.Time, minimum);
                        PassEvent set = Crossing(propagator, observer, peak.Event.Time, samples[i + 1].Event.Time, minimum);
                        passes.Add(new SatellitePass(rise, peak.Event, set, PeakUncertaintyDegrees(peak)));
                    }
                }

                i++;
            }
        }

        // Keep the passes that are up at some moment of the window. The guard samples can reveal a
        // grazing pass just outside it, which is not the caller's concern.
        var inWindow = passes.Where(p => p.Set.Time >= start && p.Rise.Time <= end).ToList();

        // An outermost sample still at or above the minimum means a rise or set was out of reach:
        // the extension ran out, or SGP4 failed before the satellite went below the minimum.
        bool startUnresolved = samples.Count > 0 && samples[0].Event.ElevationDegrees >= minimum;
        bool endUnresolved = stoppedAt is null && samples.Count > 0 && samples[^1].Event.ElevationDegrees >= minimum;
        return new PassSearchResult(inWindow, stoppedBy, stoppedAt)
        {
            AboveMinimumAtStartSince = startUnresolved ? samples[0].Event.Time : null,
            AboveMinimumAtEndUntil = endUnresolved ? samples[^1].Event.Time : null,
        };
    }

    /// <summary>
    /// Samples away from <paramref name="origin"/> in one direction: always the guard samples, then
    /// on while the satellite is at or above the minimum, up to the extension limit. Stops early if
    /// SGP4 fails. Returned in order of distance from the origin.
    /// </summary>
    private static List<Sample> Extend(
        Sgp4Propagator propagator, TopocentricFrame observer, DateTimeOffset origin, int direction, double minimum, long maxSteps)
    {
        var extension = new List<Sample>();
        for (long k = 1; k <= maxSteps; k++)
        {
            Sample? sample = Observe(propagator, observer, origin + (Step * (k * direction)));
            if (sample is null)
            {
                break;
            }

            extension.Add(sample.Value);
            if (k >= GuardSamples && sample.Value.Event.ElevationDegrees < minimum)
            {
                break;
            }
        }

        return extension;
    }

    /// <summary>Finds where elevation crosses the minimum between two instants that straddle it.</summary>
    private static PassEvent Crossing(Sgp4Propagator propagator, TopocentricFrame observer, DateTimeOffset from, DateTimeOffset to, double minimum)
    {
        double span = (to - from).TotalSeconds;
        SearchResult root = Brent.FindRoot(
            seconds => ElevationAt(propagator, observer, At(from, seconds)) - minimum,
            0.0,
            span,
            TimeToleranceSeconds);
        return Observe(propagator, observer, At(from, root.X))?.Event
            ?? throw new InvalidOperationException("SGP4 failed between two instants where it succeeded.");
    }

    /// <summary>Refines every local maximum between two sample indices and returns the highest result.</summary>
    private static Sample HighestRefinedPeak(
        Sgp4Propagator propagator, TopocentricFrame observer, List<Sample> samples, int before, int after)
    {
        Sample? best = null;
        for (int k = before + 1; k < after; k++)
        {
            if (IsLocalMaximum(samples, k))
            {
                Sample refined = Refine(propagator, observer, samples[k]);
                if (best is null || refined.Event.ElevationDegrees > best.Value.Event.ElevationDegrees)
                {
                    best = refined;
                }
            }
        }

        // A run of samples at or above the minimum always contains a local maximum.
        return best ?? throw new InvalidOperationException("No local maximum in a pass.");
    }

    private static bool IsLocalMaximum(List<Sample> samples, int k) =>
        samples[k].Event.ElevationDegrees >= samples[k - 1].Event.ElevationDegrees
        && samples[k].Event.ElevationDegrees >= samples[k + 1].Event.ElevationDegrees;

    /// <summary>Maximizes elevation within one step either side of a sample with Brent's minimizer.</summary>
    private static Sample Refine(Sgp4Propagator propagator, TopocentricFrame observer, Sample coarse)
    {
        double step = Step.TotalSeconds;
        SearchResult found = Brent.Minimize(
            seconds => -ElevationAt(propagator, observer, At(coarse.Event.Time, seconds)),
            -step,
            step,
            PeakToleranceSeconds);
        Sample? refined = Observe(propagator, observer, At(coarse.Event.Time, found.X));

        // The minimizer finds a local maximum or an end of the interval; the sample itself is a
        // floor, so the result can only improve on it.
        return refined is { } candidate && candidate.Event.ElevationDegrees > coarse.Event.ElevationDegrees ? candidate : coarse;
    }

    /// <summary>
    /// The most elevation can exceed the reported peak: the line-of-sight rate, bounded over the
    /// distance within which Brent's minimizer places the peak, times that distance.
    /// </summary>
    private static double PeakUncertaintyDegrees(Sample peak)
    {
        // Brent's bound: 2 (tolerance + √macheps |x|), with |x| at most one step.
        double placement = 2.0 * (PeakToleranceSeconds + (1.5e-8 * Step.TotalSeconds));
        return MaximumGainDegrees(peak, TimeSpan.FromSeconds(placement));
    }

    /// <summary>
    /// The most elevation can change within <paramref name="span"/> of a sample, in degrees: the
    /// relative speed (plus an allowance for its change) over the shortest range it can reach.
    /// </summary>
    private static double MaximumGainDegrees(Sample sample, TimeSpan span)
    {
        double seconds = span.TotalSeconds;
        double speed = sample.SpeedKmPerSecond + (SpeedAllowanceKmPerSecond * seconds / Step.TotalSeconds);
        double nearest = Math.Max(sample.RangeKm - (speed * seconds), 1.0);
        return speed / nearest * seconds * 180.0 / Math.PI;
    }

    private static DateTimeOffset At(DateTimeOffset origin, double seconds) =>
        origin.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    private static double ElevationAt(Sgp4Propagator propagator, TopocentricFrame observer, DateTimeOffset t) =>
        Observe(propagator, observer, t)?.Event.ElevationDegrees
        ?? throw new InvalidOperationException($"SGP4 failed at {t:O} inside a pass, between instants where it succeeded.");

    private static Sample? Observe(Sgp4Propagator propagator, TopocentricFrame observer, DateTimeOffset t)
    {
        PropagationResult result = propagator.Propagate(t);
        if (!result.Succeeded)
        {
            return null;
        }

        EcefState ecef = EarthRotation.TemeToEcef(result.State, t);
        LookAngles look = observer.LookAt(ecef);
        return new Sample(new PassEvent(t, look.AzimuthDegrees, look.ElevationDegrees), look.RangeKm, ecef.Velocity.Length);
    }

    /// <summary>One observation: where the satellite appears, its range, and its speed relative to the Earth.</summary>
    private readonly record struct Sample(PassEvent Event, double RangeKm, double SpeedKmPerSecond);
}
