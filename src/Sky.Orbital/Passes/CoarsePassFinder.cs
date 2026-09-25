using Sky.Orbital.Frames;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Passes;

/// <summary>
/// Finds passes by sampling elevation every 10 seconds, then refines each peak in 0.1 s steps.
/// This is Milestone 1's deliberately simple finder; Milestone 2 replaces it with root-finding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rise and set.</b> Rise is the first 10 s sample at or above the minimum elevation, so it is up
/// to one step after the true crossing; set is the last such sample, up to one step before. If the
/// refined peak falls outside those samples (a grazing pass), rise or set is moved to the peak,
/// which keeps rise ≤ peak ≤ set and keeps both bounds, since the true crossing lies between the
/// previous sample and the peak.
/// </para>
/// <para>
/// <b>Peak.</b> Every local maximum among the 10 s samples of a pass is searched in 0.1 s steps
/// across one coarse step either side, and the highest result is the culmination. Elevation has no
/// two maxima within 20 s of each other for any orbit, so each true maximum lies inside one of those
/// searches, and the reported peak is within 0.1 s of the highest one. A pass can have several
/// maxima; high, eccentric orbits often do. The shortfall is at most the line of sight's angular rate
/// times 0.05 s. <see cref="SatellitePass.PeakElevationUncertaintyDegrees"/> bounds that rate at the
/// reported peak by the satellite's speed relative to the observer over the range, allowing for the
/// most either can change in 0.1 s. It comes from the pass itself, so it holds for any orbit and any
/// element age: about 0.05 degrees for an overhead ISS pass, 0.02 for a 15 degree pass.
/// </para>
/// <para>
/// <b>Grazing passes.</b> A pass can clear the minimum between two samples without any sample doing
/// so. Every local maximum below the minimum that could rise above it is refined too. Between samples
/// elevation can gain at most the line-of-sight rate, bounded as above over 10 s, times 10 s.
/// </para>
/// <para>
/// Only complete passes are reported: a pass already above the minimum at the start, or still above
/// it at the end, is left out. If SGP4 fails, for example because the satellite has decayed, sampling
/// stops there and the result says when and why. Elevation is geometric, with no refraction, and UTC
/// is treated as UT1 (assumption A5).
/// </para>
/// </remarks>
public static class CoarsePassFinder
{
    /// <summary>The sampling step for rise and set.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    /// <summary>The step used to refine each peak.</summary>
    public static readonly TimeSpan PeakStep = TimeSpan.FromSeconds(0.1);

    // Upper bound on how much the relative speed can change in 10 s, km/s. Gravity changes a low
    // orbit's speed by under 0.01 km/s per second; 0.1 km/s over 10 s is ample.
    private const double SpeedAllowanceKmPerSecond = 0.1;

    /// <summary>Finds complete passes between two instants.</summary>
    /// <param name="propagator">The satellite.</param>
    /// <param name="observer">The observer's local frame.</param>
    /// <param name="start">Start of the search, inclusive.</param>
    /// <param name="end">End of the search, inclusive.</param>
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

        var samples = new List<Sample>();
        Sgp4Error stoppedBy = Sgp4Error.None;
        DateTimeOffset? stoppedAt = null;
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
        }

        var passes = new List<SatellitePass>();
        int i = 0;
        while (i < samples.Count)
        {
            if (samples[i].Event.ElevationDegrees >= minimumElevationDegrees)
            {
                int first = i;
                while (i < samples.Count && samples[i].Event.ElevationDegrees >= minimumElevationDegrees)
                {
                    i++;
                }

                int last = i - 1;
                if (first == 0 || i == samples.Count)
                {
                    continue; // already up at the start, or still up at the end: not a complete pass
                }

                Sample peak = HighestRefinedPeak(propagator, observer, samples, first - 1, last + 1);
                PassEvent rise = samples[first].Event;
                PassEvent set = samples[last].Event;
                if (peak.Event.Time < rise.Time)
                {
                    rise = peak.Event;
                }

                if (peak.Event.Time > set.Time)
                {
                    set = peak.Event;
                }

                passes.Add(new SatellitePass(rise, peak.Event, set, PeakUncertaintyDegrees(peak)));
            }
            else
            {
                if (i > 0 && i < samples.Count - 1 && IsLocalMaximum(samples, i)
                    && samples[i].Event.ElevationDegrees + MaximumGainDegrees(samples[i], Step) >= minimumElevationDegrees)
                {
                    Sample peak = Refine(propagator, observer, samples[i]);
                    if (peak.Event.ElevationDegrees >= minimumElevationDegrees)
                    {
                        passes.Add(new SatellitePass(peak.Event, peak.Event, peak.Event, PeakUncertaintyDegrees(peak)));
                    }
                }

                i++;
            }
        }

        return new PassSearchResult(passes, stoppedBy, stoppedAt);
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

    /// <summary>Searches one coarse step either side of a sample in 0.1 s steps.</summary>
    private static Sample Refine(Sgp4Propagator propagator, TopocentricFrame observer, Sample coarse)
    {
        Sample best = coarse;
        long steps = Step.Ticks / PeakStep.Ticks;
        for (long k = -steps; k <= steps; k++)
        {
            Sample? sample = Observe(propagator, observer, coarse.Event.Time + (PeakStep * k));
            if (sample is { } candidate && candidate.Event.ElevationDegrees > best.Event.ElevationDegrees)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The most elevation can exceed the reported peak: the line-of-sight rate, bounded over the
    /// 0.1 s either side where the true peak can lie, times the 0.05 s to the nearer sample.
    /// </summary>
    private static double PeakUncertaintyDegrees(Sample peak) =>
        MaximumGainDegrees(peak, PeakStep) / 2.0;

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
