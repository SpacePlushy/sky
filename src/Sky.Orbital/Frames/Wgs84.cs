namespace Sky.Orbital.Frames;

/// <summary>Conversions between Earth-fixed coordinates and WGS-84 geodetic coordinates.</summary>
public static class Wgs84
{
    /// <summary>Equatorial radius, in kilometers.</summary>
    public const double EquatorialRadius = 6378.137;

    /// <summary>Flattening, 1 / 298.257223563.</summary>
    public const double Flattening = 1.0 / 298.257223563;

    /// <summary>First eccentricity squared, f (2 - f).</summary>
    public const double EccentricitySquared = Flattening * (2.0 - Flattening);

    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    // Vermeille's closed form needs the point outside a small region around the center, where
    // geodetic coordinates stop being unique (radius a * e^2, about 42.7 km). 1000 km is far
    // inside any real observer or satellite position.
    private const double MinimumRadius = 1000.0;

    /// <summary>Converts geodetic coordinates to an Earth-fixed position in kilometers.</summary>
    public static Vec3 ToEcef(Geodetic geodetic)
    {
        double lat = geodetic.LatitudeDegrees * DegreesToRadians;
        double lon = geodetic.LongitudeDegrees * DegreesToRadians;
        double sinLat = Math.Sin(lat);
        double cosLat = Math.Cos(lat);

        // Radius of curvature in the prime vertical.
        double n = EquatorialRadius / Math.Sqrt(1.0 - (EccentricitySquared * sinLat * sinLat));
        double h = geodetic.HeightKm;

        return new Vec3(
            (n + h) * cosLat * Math.Cos(lon),
            (n + h) * cosLat * Math.Sin(lon),
            ((n * (1.0 - EccentricitySquared)) + h) * sinLat);
    }

    /// <summary>Converts an Earth-fixed position in kilometers to geodetic coordinates.</summary>
    /// <remarks>
    /// Exact closed-form solution from H. Vermeille, "Computing geodetic coordinates from
    /// geocentric coordinates", Journal of Geodesy 78 (2004) 94-95. No iteration and no
    /// convergence threshold. Longitude is 0 at the poles, where it is undefined.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The point is within 1000 km of the center.</exception>
    public static Geodetic FromEcef(Vec3 position)
    {
        double x = position.X;
        double y = position.Y;
        double z = position.Z;
        double rhoSquared = (x * x) + (y * y);
        if (rhoSquared + (z * z) < MinimumRadius * MinimumRadius)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position), position, "Geodetic coordinates need a point at least 1000 km from the Earth's center.");
        }

        const double a2 = EquatorialRadius * EquatorialRadius;
        const double e2 = EccentricitySquared;
        const double e4 = e2 * e2;

        double p = rhoSquared / a2;
        double q = (1.0 - e2) * z * z / a2;
        double r = (p + q - e4) / 6.0;
        double s = e4 * p * q / (4.0 * r * r * r);
        double t = Math.Cbrt(1.0 + s + Math.Sqrt(s * (2.0 + s)));
        double u = r * (1.0 + t + (1.0 / t));
        double v = Math.Sqrt((u * u) + (e4 * q));
        double w = e2 * (u + v - q) / (2.0 * v);
        double k = Math.Sqrt(u + v + (w * w)) - w;
        double d = k * Math.Sqrt(rhoSquared) / (k + e2);
        double hypotenuse = Math.Sqrt((d * d) + (z * z));

        double latitude = 2.0 * Math.Atan2(z, d + hypotenuse);
        double height = (k + e2 - 1.0) / k * hypotenuse;
        // atan2 returns -pi for y = -0.0 with x negative; the documented range is (-180, 180].
        double longitudeDegrees = Math.Atan2(y, x) * RadiansToDegrees;
        if (longitudeDegrees <= -180.0)
        {
            longitudeDegrees = 180.0;
        }

        return new Geodetic(latitude * RadiansToDegrees, longitudeDegrees, height);
    }
}
