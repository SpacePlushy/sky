using Sky.Orbital.Frames;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Passes;

/// <summary>
/// Finds passes by sampling elevation every 10 seconds, then refines each peak in 0.1 s steps.
/// This is Milestone 1's deliberately simple finder; Milestone 2 replaces it with root-finding.
/// </summary>
/// <remarks>
/// <para>
/// Rise is the first 10 s sample at or above the minimum elevation, so it is up to one step after
/// the true crossing; set is the last such sample, up to one step before it.
/// </para>
/// <para>
/// The peak is refined because elevation near the zenith changes about 1 degree per second, so a
/// 10 s grid alone could miss an overhead peak by up to 5 degrees. Elevation during a pass has a
/// single maximum, which lies within one coarse step of the highest coarse sample. Searching that
/// 20 s span in 0.1 s steps puts the reported peak within 0.1 s of the true one, and at most
/// 0.054 degrees below it: the line of sight turns at most 7.7 km/s / 410 km = 1.08 deg/s.
/// </para>
/// <para>
/// Only complete passes are reported: a pass already above the minimum at the start, or still
/// above it at the end, is left out. If SGP4 fails, for example because the satellite has decayed,
/// the search stops and the result says when and why. Elevation is geometric, with no refraction,
/// and UTC is treated as UT1 (assumption A5).
/// </para>
/// </remarks>
public static class CoarsePassFinder
{
    /// <summary>The sampling step for rise and set.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    /// <summary>The step used to refine each peak.</summary>
    public static readonly TimeSpan PeakStep = TimeSpan.FromSeconds(0.1);

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

        var passes = new List<SatellitePass>();
        bool? previousAbove = null;
        PassEvent? rise = null;
        PassEvent peak = default;
        PassEvent lastAbove = default;

        for (DateTimeOffset t = start; t <= end; t += Step)
        {
            PropagationResult result = propagator.Propagate(t);
            if (!result.Succeeded)
            {
                return new PassSearchResult(passes, result.Error, t);
            }

            PassEvent sample = Observe(observer, result.State, t);
            bool above = sample.ElevationDegrees >= minimumElevationDegrees;

            if (above)
            {
                if (previousAbove == false)
                {
                    rise = sample;
                    peak = sample;
                }
                else if (rise is not null && sample.ElevationDegrees > peak.ElevationDegrees)
                {
                    peak = sample;
                }

                lastAbove = sample;
            }
            else if (rise is { } risen)
            {
                passes.Add(new SatellitePass(risen, RefinePeak(propagator, observer, peak), lastAbove));
                rise = null;
            }

            previousAbove = above;
        }

        return new PassSearchResult(passes, Sgp4Error.None, null);
    }

    /// <summary>Searches one coarse step either side of the highest coarse sample in 0.1 s steps.</summary>
    private static PassEvent RefinePeak(Sgp4Propagator propagator, TopocentricFrame observer, PassEvent coarsePeak)
    {
        PassEvent best = coarsePeak;
        long steps = Step.Ticks / PeakStep.Ticks;
        for (long i = -steps; i <= steps; i++)
        {
            DateTimeOffset t = coarsePeak.Time + (PeakStep * i);
            PropagationResult result = propagator.Propagate(t);
            if (!result.Succeeded)
            {
                continue;
            }

            PassEvent sample = Observe(observer, result.State, t);
            if (sample.ElevationDegrees > best.ElevationDegrees)
            {
                best = sample;
            }
        }

        return best;
    }

    private static PassEvent Observe(TopocentricFrame observer, TemeState state, DateTimeOffset t)
    {
        LookAngles look = observer.LookAt(EarthRotation.TemeToEcef(state, t));
        return new PassEvent(t, look.AzimuthDegrees, look.ElevationDegrees);
    }
}
