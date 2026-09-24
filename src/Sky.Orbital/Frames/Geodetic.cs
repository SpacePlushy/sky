namespace Sky.Orbital.Frames;

/// <summary>Geodetic coordinates on the WGS-84 ellipsoid.</summary>
/// <param name="LatitudeDegrees">Geodetic latitude in degrees, positive north.</param>
/// <param name="LongitudeDegrees">Longitude in degrees, positive east, in (-180, 180].</param>
/// <param name="HeightKm">Height above the ellipsoid along its normal, in kilometers.</param>
public readonly record struct Geodetic(double LatitudeDegrees, double LongitudeDegrees, double HeightKm);
