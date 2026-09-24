using Sky.Orbital.Propagation;
using Sky.Orbital.Time;

namespace Sky.Orbital.Frames;

/// <summary>Converts SGP4's TEME output to Earth-fixed coordinates.</summary>
/// <remarks>
/// TEME to Earth-fixed is a single rotation about the z axis by Greenwich mean sidereal time
/// (IAU-82), as defined in Vallado et al. 2006, AIAA 2006-6753. The result is the pseudo
/// Earth-fixed (PEF) frame: ITRF without polar motion, which Sky ignores (assumption A4,
/// about 12 m at low-Earth-orbit radius).
/// </remarks>
public static class EarthRotation
{
    /// <summary>Rotates a TEME state into the Earth-fixed frame at an instant given in UTC.</summary>
    /// <param name="teme">Position (km) and velocity (km/s) in TEME.</param>
    /// <param name="utc">The instant of the state.</param>
    /// <param name="ut1MinusUtcSeconds">
    /// UT1 minus UTC in seconds. Defaults to zero: Sky treats UTC as UT1 (assumption A5),
    /// which is never more than 0.9 s off by definition.
    /// </param>
    public static EcefState TemeToEcef(TemeState teme, DateTimeOffset utc, double ut1MinusUtcSeconds = 0.0)
    {
        var jdUtc = JulianDate.FromInstant(utc);
        var jdUt1 = jdUtc with { Fraction = jdUtc.Fraction + (ut1MinusUtcSeconds / 86400.0) };
        return TemeToEcef(teme, jdUt1);
    }

    /// <summary>Rotates a TEME state into the Earth-fixed frame at a UT1 Julian date.</summary>
    /// <param name="teme">Position (km) and velocity (km/s) in TEME.</param>
    /// <param name="ut1">The instant of the state, as a UT1 Julian date.</param>
    public static EcefState TemeToEcef(TemeState teme, JulianDate ut1)
    {
        double gmst = SiderealTime.GreenwichMean(ut1);
        double rate = SiderealTime.GreenwichMeanRate(ut1);
        double cos = Math.Cos(gmst);
        double sin = Math.Sin(gmst);

        Vec3 r = teme.Position;
        Vec3 v = teme.Velocity;
        var position = new Vec3((cos * r.X) + (sin * r.Y), (-sin * r.X) + (cos * r.Y), r.Z);
        var rotatedVelocity = new Vec3((cos * v.X) + (sin * v.Y), (-sin * v.X) + (cos * v.Y), v.Z);

        // Velocity relative to the rotating Earth: subtract omega x r, with omega = (0, 0, rate).
        // Using the rate of the GMST angle itself makes the result exactly the time derivative
        // of the Earth-fixed position above. Vallado's teme2ecef uses the Earth's inertial rate
        // instead, which is lower by the precession of the equinox (7.1e-12 rad/s, 0.05 mm/s at
        // low-Earth-orbit radius). Skyfield uses the GMST rate, as here.
        var velocity = new Vec3(
            rotatedVelocity.X + (rate * position.Y),
            rotatedVelocity.Y - (rate * position.X),
            rotatedVelocity.Z);

        return new EcefState(position, velocity);
    }
}
