namespace Sky.Orbital.Passes;

/// <summary>A moment in a pass: when, and where the satellite appears.</summary>
public readonly record struct PassEvent(DateTimeOffset Time, double AzimuthDegrees, double ElevationDegrees);

/// <summary>One pass of a satellite above an observer's minimum elevation.</summary>
/// <param name="Rise">The first known moment at or above the minimum elevation.</param>
/// <param name="Culmination">The highest point found.</param>
/// <param name="Set">The last known moment at or above the minimum elevation.</param>
/// <param name="PeakElevationUncertaintyDegrees">
/// How far the true peak elevation can exceed <see cref="Culmination"/>'s, from this pass's own
/// geometry at the peak. See <see cref="CoarsePassFinder"/>.
/// </param>
public sealed record SatellitePass(PassEvent Rise, PassEvent Culmination, PassEvent Set, double PeakElevationUncertaintyDegrees);
