namespace Sky.Orbital.Passes;

/// <summary>A moment in a pass: when, and where the satellite appears.</summary>
public readonly record struct PassEvent(DateTimeOffset Time, double AzimuthDegrees, double ElevationDegrees);

/// <summary>One pass of a satellite above an observer's minimum elevation.</summary>
/// <param name="Rise">Where elevation crosses the minimum going up, within 1 ms. It can be before the search started.</param>
/// <param name="Culmination">The highest point.</param>
/// <param name="Set">Where elevation crosses the minimum going down, within 1 ms. It can be after the search ended.</param>
/// <param name="PeakElevationUncertaintyDegrees">
/// How far the true peak elevation can exceed <see cref="Culmination"/>'s, from this pass's own
/// geometry at the peak. See <see cref="PassFinder"/>.
/// </param>
public sealed record SatellitePass(PassEvent Rise, PassEvent Culmination, PassEvent Set, double PeakElevationUncertaintyDegrees);
