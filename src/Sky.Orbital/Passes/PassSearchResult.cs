using Sky.Orbital.Propagation;

namespace Sky.Orbital.Passes;

/// <summary>The passes found in a search, and whether SGP4 cut the search short.</summary>
/// <param name="Passes">Complete passes, in time order.</param>
/// <param name="StoppedBy">
/// <see cref="Sgp4Error.None"/> if the whole window was searched; otherwise the SGP4 error, such as
/// <see cref="Sgp4Error.Decayed"/>, that ended the search.
/// </param>
/// <param name="StoppedAt">When SGP4 failed, or null if the whole window was searched.</param>
public sealed record PassSearchResult(IReadOnlyList<SatellitePass> Passes, Sgp4Error StoppedBy, DateTimeOffset? StoppedAt);
