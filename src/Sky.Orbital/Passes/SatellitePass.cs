namespace Sky.Orbital.Passes;

/// <summary>A moment in a pass: when, and where the satellite appears.</summary>
public readonly record struct PassEvent(DateTimeOffset Time, double AzimuthDegrees, double ElevationDegrees);

/// <summary>One pass of a satellite above an observer's minimum elevation.</summary>
public sealed record SatellitePass(PassEvent Rise, PassEvent Culmination, PassEvent Set);
