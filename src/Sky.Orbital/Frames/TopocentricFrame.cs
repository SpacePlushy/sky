namespace Sky.Orbital.Frames;

/// <summary>
/// An observer's local horizon frame: east, north, and up axes at a fixed point on the Earth.
/// Build one per observer and reuse it; construction does the trigonometry once.
/// </summary>
public sealed class TopocentricFrame
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    private readonly Vec3 _origin;
    private readonly Vec3 _east;
    private readonly Vec3 _north;
    private readonly Vec3 _up;

    /// <summary>Creates the frame for an observer at a WGS-84 geodetic position.</summary>
    public TopocentricFrame(Geodetic observer)
    {
        Observer = observer;
        _origin = Wgs84.ToEcef(observer);

        double lat = observer.LatitudeDegrees * DegreesToRadians;
        double lon = observer.LongitudeDegrees * DegreesToRadians;
        double sinLat = Math.Sin(lat);
        double cosLat = Math.Cos(lat);
        double sinLon = Math.Sin(lon);
        double cosLon = Math.Cos(lon);

        // Up is the ellipsoid normal, so elevation is measured from the geodetic horizon.
        _east = new Vec3(-sinLon, cosLon, 0.0);
        _north = new Vec3(-sinLat * cosLon, -sinLat * sinLon, cosLat);
        _up = new Vec3(cosLat * cosLon, cosLat * sinLon, sinLat);
    }

    /// <summary>Maps an atan2 result in degrees, in [-180, 180], into [0, 360).</summary>
    /// <remarks>A tiny negative angle becomes exactly 360.0 when shifted, because the sum rounds.</remarks>
    internal static double NormalizeAzimuthDegrees(double degrees)
    {
        double azimuth = degrees < 0.0 ? degrees + 360.0 : degrees;
        return azimuth >= 360.0 || azimuth == 0.0 ? 0.0 : azimuth; // also turns -0.0 into 0.0
    }

    /// <summary>The observer's position.</summary>
    public Geodetic Observer { get; }

    /// <summary>Look angles to a satellite given in Earth-fixed coordinates.</summary>
    /// <remarks>
    /// The observer is fixed to the Earth, so the satellite's Earth-fixed velocity is also its
    /// velocity relative to the observer. Elevation uses atan2 rather than asin so it keeps full
    /// precision near the zenith. At the zenith azimuth is undefined, and the value returned there
    /// depends on rounding.
    /// </remarks>
    public LookAngles LookAt(EcefState satellite)
    {
        Vec3 lineOfSight = satellite.Position - _origin;
        double east = lineOfSight.Dot(_east);
        double north = lineOfSight.Dot(_north);
        double up = lineOfSight.Dot(_up);

        double range = lineOfSight.Length;
        double horizontal = Math.Sqrt((east * east) + (north * north));
        double elevation = Math.Atan2(up, horizontal) * RadiansToDegrees;
        double azimuth = NormalizeAzimuthDegrees(Math.Atan2(east, north) * RadiansToDegrees);

        double rangeRate = lineOfSight.Dot(satellite.Velocity) / range;
        return new LookAngles(azimuth, elevation, range, rangeRate);
    }
}
