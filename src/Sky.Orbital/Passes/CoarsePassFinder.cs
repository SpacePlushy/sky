using Sky.Orbital.Frames;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Passes;

/// <summary>
/// Finds passes by sampling elevation every 10 seconds. This is Milestone 1's deliberately
/// simple finder; Milestone 2 replaces it with root-finding.
/// </summary>
/// <remarks>
/// Accuracy follows from the step. Rise is the first sample at or above the minimum, so it is up
/// to one step after the true crossing; set is the last such sample, up to one step before. The
/// culmination is the highest sample, within one step of the true peak and slightly below it.
/// Only complete passes are reported: a pass already above the minimum at the start, or still
/// above it at the end, is left out. Sampling stops if SGP4 reports an error such as decay.
/// Passes shorter than the step could be missed; low-orbit passes above 10 degrees last minutes.
/// Elevation is geometric, with no refraction, and UTC is treated as UT1 (assumption A5).
/// </remarks>
public static class CoarsePassFinder
{
    /// <summary>The sampling step.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    /// <summary>Finds complete passes between two instants.</summary>
    /// <param name="propagator">The satellite.</param>
    /// <param name="observer">The observer's local frame.</param>
    /// <param name="start">Start of the search, inclusive.</param>
    /// <param name="end">End of the search, inclusive.</param>
    /// <param name="minimumElevationDegrees">Elevation a pass must reach, in degrees.</param>
    public static IReadOnlyList<SatellitePass> Find(
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
                break;
            }

            LookAngles look = observer.LookAt(EarthRotation.TemeToEcef(result.State, t));
            var sample = new PassEvent(t, look.AzimuthDegrees, look.ElevationDegrees);
            bool above = look.ElevationDegrees >= minimumElevationDegrees;

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
                passes.Add(new SatellitePass(risen, peak, lastAbove));
                rise = null;
            }

            previousAbove = above;
        }

        return passes;
    }
}
