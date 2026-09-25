using Sky.Orbital.Propagation;

namespace Sky.Orbital.Passes;

/// <summary>The passes found in a search, and anything that kept the search from being complete.</summary>
/// <param name="Passes">Every complete pass that is up at some moment of the search window, in time order.</param>
/// <param name="StoppedBy">
/// <see cref="Sgp4Error.None"/> if the whole window was searched; otherwise the SGP4 error, such as
/// <see cref="Sgp4Error.Decayed"/>, that ended the search.
/// </param>
/// <param name="StoppedAt">When SGP4 failed, or null if the whole window was searched.</param>
public sealed record PassSearchResult(IReadOnlyList<SatellitePass> Passes, Sgp4Error StoppedBy, DateTimeOffset? StoppedAt)
{
    /// <summary>
    /// Set when the satellite was already above the minimum at the start and its rise could not be
    /// found: it stayed up for the whole backward extension, or SGP4 failed first. The value is the
    /// earliest instant checked, at which it was still up. That pass is not in <see cref="Passes"/>.
    /// </summary>
    public DateTimeOffset? AboveMinimumAtStartSince { get; init; }

    /// <summary>
    /// Set when the satellite was still above the minimum at the end and its set could not be found.
    /// The value is the latest instant checked, at which it was still up. That pass is not in
    /// <see cref="Passes"/>.
    /// </summary>
    public DateTimeOffset? AboveMinimumAtEndUntil { get; init; }
}
