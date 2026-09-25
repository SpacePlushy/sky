namespace Sky.Orbital.Frames;

/// <summary>Where a satellite appears from an observer on the Earth.</summary>
/// <param name="AzimuthDegrees">Azimuth, clockwise from true north, in [0, 360).</param>
/// <param name="ElevationDegrees">Geometric elevation above the local horizontal, in [-90, 90]. No refraction.</param>
/// <param name="RangeKm">Distance from observer to satellite, in kilometers.</param>
/// <param name="RangeRateKmPerSecond">Rate of change of range, in km/s. Positive when receding.</param>
public readonly record struct LookAngles(double AzimuthDegrees, double ElevationDegrees, double RangeKm, double RangeRateKmPerSecond);
