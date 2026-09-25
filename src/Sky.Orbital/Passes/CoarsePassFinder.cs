using Sky.Orbital.Elements;
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
/// 20 s span in 0.1 s steps puts the reported peak within 0.1 s of the true one, and below it by
/// at most <see cref="PeakElevationBoundDegrees"/>, which depends on how low the orbit is: 0.061
/// degrees for the ISS.
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

    /// <summary>
    /// The most a reported peak elevation can fall short of the true peak for this satellite, in
    /// degrees, for any observer allowed by the settings (up to 9 km above the ellipsoid).
    /// </summary>
    /// <remarks>
    /// The reported peak is the highest 0.1 s sample, so it is within 0.05 s of the nearer sample
    /// bracketing the true peak, and falls short by at most the fastest the line of sight can turn,
    /// times 0.05 s. That rate is at most the satellite's speed relative to the Earth divided by
    /// the shortest possible range:
    /// <list type="bullet">
    /// <item>Radii come from the mean elements (a from mean motion, WGS-72 mu), widened by 25 km
    /// each way to cover SGP4's short-period swings, the mean-motion convention, and a week of drag.</item>
    /// <item>Speed is vis-viva at the lowest radius, plus Earth rotation at the highest.</item>
    /// <item>Range is the lowest radius minus the largest observer radius, 6378.137 km + 9 km.</item>
    /// </list>
    /// For the ISS this gives 0.062 degrees; an exactly overhead pass measures 0.0495.
    /// </remarks>
    public static double PeakElevationBoundDegrees(MeanElements elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        const double mu = 398600.8;                   // km^3/s^2, WGS-72 as SGP4 uses
        const double earthRotation = 7.292115855e-5;  // rad/s, GMST rate
        const double marginKm = 25.0;
        const double largestObserverRadiusKm = 6378.137 + 9.0;

        double n = elements.MeanMotion * 2.0 * Math.PI / 86400.0;
        double a = Math.Cbrt(mu / (n * n));
        double lowest = (a * (1.0 - elements.Eccentricity)) - marginKm;
        double highest = (a * (1.0 + elements.Eccentricity)) + marginKm;
        double speed = Math.Sqrt(mu * ((2.0 / lowest) - (1.0 / a))) + (earthRotation * highest);
        double nearest = Math.Max(lowest - largestObserverRadiusKm, 1.0);
        double halfStep = PeakStep.TotalSeconds / 2.0;
        return speed / nearest * halfStep * 180.0 / Math.PI;
    }

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
