using Sky.Orbital.Frames;

namespace Sky.Orbital.Astronomy;

/// <summary>
/// Whether a satellite is in sunlight: the Earth's geometric shadow for the Sun's center, with the
/// Earth as the WGS-84 ellipsoid and no atmosphere (assumption A11).
/// </summary>
/// <remarks>
/// <para>
/// A satellite is sunlit when the straight line from it toward the Sun's center does not touch the
/// ellipsoid. Stretching the z axis by a/b turns the ellipsoid into a sphere of radius a, and
/// because stretching maps lines to lines and the ellipsoid onto the sphere, the line touches the
/// ellipsoid exactly when the stretched line touches the sphere. The test is therefore exact.
/// </para>
/// <para>
/// <see cref="Function"/> is a continuous signed version of that test, for root-finding: in the
/// stretched space it is the line's closest approach to the center minus a, and when the line
/// heads away from the center (the Sun is on the satellite's side) it is the satellite's own
/// distance minus a. The two agree where they meet, so the function has no jump, and it is zero
/// exactly where the line grazes the ellipsoid: the moment the Sun's center sets or rises for the
/// satellite. The line runs to the Sun, not beyond it, but at 1 AU the difference is immaterial.
/// </para>
/// </remarks>
public static class EarthShadow
{
    /// <summary>The WGS-84 polar radius over the equatorial radius, b/a = 1 − f.</summary>
    private const double AxisRatio = 1.0 - Wgs84.Flattening;

    /// <summary>
    /// Positive when the satellite is sunlit, negative in the shadow, and zero on the shadow's edge.
    /// Its magnitude is a distance in kilometers in the stretched space described above, not a
    /// physical distance.
    /// </summary>
    /// <param name="satelliteEcef">The satellite's Earth-fixed position, km.</param>
    /// <param name="sunEcef">The Sun's Earth-fixed position, km, in the same frame.</param>
    public static double Function(Vec3 satelliteEcef, Vec3 sunEcef)
    {
        Vec3 s = Stretch(satelliteEcef);
        Vec3 toSun = Stretch(sunEcef - satelliteEcef);
        double length = toSun.Length;
        if (!(length > 0))
        {
            throw new ArgumentException("The satellite and the Sun are at the same place.");
        }

        Vec3 u = toSun * (1.0 / length);

        // Parameter of the closest point to the center along s + λu.
        double along = -s.Dot(u);
        if (along <= 0)
        {
            return s.Length - Wgs84.EquatorialRadius;
        }

        Vec3 closest = s + (u * along);
        return closest.Length - Wgs84.EquatorialRadius;
    }

    /// <summary>True when the Sun's center is geometrically visible from the satellite.</summary>
    public static bool IsSunlit(Vec3 satelliteEcef, Vec3 sunEcef) => Function(satelliteEcef, sunEcef) > 0;

    private static Vec3 Stretch(Vec3 v) => new(v.X, v.Y, v.Z / AxisRatio);
}
