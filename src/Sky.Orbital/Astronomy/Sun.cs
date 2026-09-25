using Sky.Orbital.Time;

namespace Sky.Orbital.Astronomy;

/// <summary>The Sun's apparent place: right ascension and declination of date, and distance.</summary>
/// <param name="RightAscensionDegrees">Apparent right ascension, referred to the true equinox of date, in [0, 360).</param>
/// <param name="DeclinationDegrees">Apparent declination.</param>
/// <param name="DistanceAu">Distance from the Earth's center, in astronomical units.</param>
/// <param name="EquationOfEquinoxesRadians">
/// Nutation in longitude times the cosine of the obliquity: the angle from the mean to the true
/// equinox along the equator, from the same one-term nutation the position uses.
/// </param>
public readonly record struct SunPlace(double RightAscensionDegrees, double DeclinationDegrees, double DistanceAu, double EquationOfEquinoxesRadians);

/// <summary>
/// The Sun's position by the low-accuracy method of J. Meeus, <i>Astronomical Algorithms</i>, 2nd
/// edition (Willmann-Bell, 1998), chapter 25: about 0.01 degrees.
/// </summary>
/// <remarks>
/// <para>
/// Meeus's method gives the apparent place: it includes aberration (−20.5″) and the main term of
/// nutation (−17.2″ sin Ω in longitude, +9.2″ cos Ω in obliquity), so it is referred to the true
/// equator and equinox of date. <see cref="PositionEcef"/> turns it into the pseudo Earth-fixed
/// frame that <see cref="Frames.EarthRotation"/> produces for satellites, by rotating it through
/// Greenwich apparent sidereal time: Sky's IAU-82 GMST plus the equation of the equinoxes from
/// the same nutation term. Satellite and Sun vectors can then be subtracted directly.
/// </para>
/// <para>
/// The formulas take Terrestrial Time. <see cref="PositionEcef"/> passes UTC instead (assumption
/// A10): the Sun moves about 0.04 degrees an hour, so TT − UTC (69.184 s in 2026) moves it by
/// 0.0008 degrees, which is inside the method's own accuracy and avoids a leap-second table.
/// </para>
/// </remarks>
public static class Sun
{
    /// <summary>The astronomical unit in kilometers (IAU 2012 Resolution B2, exact).</summary>
    public const double AstronomicalUnitKm = 149_597_870.7;

    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double DaysPerCentury = 36525.0;

    /// <summary>The Sun's apparent place at an instant of Terrestrial Time.</summary>
    /// <param name="tt">The instant as a Terrestrial Time Julian date.</param>
    public static SunPlace Apparent(JulianDate tt)
    {
        double t = tt.DaysSinceJ2000 / DaysPerCentury;

        // Meeus (25.2), (25.3), (25.4): geometric mean longitude, mean anomaly, and eccentricity.
        double meanLongitude = 280.46646 + (36000.76983 * t) + (0.0003032 * t * t);
        double meanAnomaly = (357.52911 + (35999.05029 * t) - (0.0001537 * t * t)) * DegreesToRadians;
        double eccentricity = 0.016708634 - (0.000042037 * t) - (0.0000001267 * t * t);

        // Equation of the center, true longitude, and true anomaly.
        double center = ((1.914602 - (0.004817 * t) - (0.000014 * t * t)) * Math.Sin(meanAnomaly))
            + ((0.019993 - (0.000101 * t)) * Math.Sin(2.0 * meanAnomaly))
            + (0.000289 * Math.Sin(3.0 * meanAnomaly));
        double trueLongitude = meanLongitude + center;
        double trueAnomaly = meanAnomaly + (center * DegreesToRadians);

        // Meeus (25.5): radius vector.
        double distance = 1.000001018 * (1.0 - (eccentricity * eccentricity)) / (1.0 + (eccentricity * Math.Cos(trueAnomaly)));

        // Apparent longitude: aberration (−0.00569°) and nutation in longitude (−0.00478° sin Ω).
        double omega = (125.04 - (1934.136 * t)) * DegreesToRadians;
        double nutationInLongitude = -0.00478 * Math.Sin(omega);
        double apparentLongitude = (trueLongitude - 0.00569 + nutationInLongitude) * DegreesToRadians;

        // Meeus (22.2): mean obliquity, then the nutation in obliquity (25.8).
        double meanObliquity = 23.0 + (26.0 / 60.0) + (21.448 / 3600.0)
            - (((46.8150 * t) + (0.00059 * t * t) - (0.001813 * t * t * t)) / 3600.0);
        double obliquity = (meanObliquity + (0.00256 * Math.Cos(omega))) * DegreesToRadians;

        // Meeus (25.6), (25.7): apparent right ascension and declination.
        double rightAscension = Math.Atan2(Math.Cos(obliquity) * Math.Sin(apparentLongitude), Math.Cos(apparentLongitude)) * RadiansToDegrees;
        double declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(apparentLongitude)) * RadiansToDegrees;

        double equationOfEquinoxes = nutationInLongitude * DegreesToRadians * Math.Cos(obliquity);
        return new SunPlace(rightAscension < 0 ? rightAscension + 360.0 : rightAscension, declination, distance, equationOfEquinoxes);
    }

    /// <summary>
    /// The Sun's position from the Earth's center in the pseudo Earth-fixed frame (km), the frame
    /// <see cref="Frames.EarthRotation.TemeToEcef(Propagation.TemeState, DateTimeOffset, double)"/>
    /// puts satellites in.
    /// </summary>
    /// <param name="utc">The instant. It is used as Terrestrial Time for the Sun (assumption A10) and as UT1 for the Earth's rotation (assumption A5).</param>
    public static Vec3 PositionEcef(DateTimeOffset utc)
    {
        var jd = JulianDate.FromInstant(utc);
        SunPlace place = Apparent(jd);

        double alpha = place.RightAscensionDegrees * DegreesToRadians;
        double delta = place.DeclinationDegrees * DegreesToRadians;
        double r = place.DistanceAu * AstronomicalUnitKm;
        var trueOfDate = new Vec3(r * Math.Cos(delta) * Math.Cos(alpha), r * Math.Cos(delta) * Math.Sin(alpha), r * Math.Sin(delta));

        // Greenwich apparent sidereal time turns the true equinox into the Greenwich meridian.
        double gast = SiderealTime.GreenwichMean(jd) + place.EquationOfEquinoxesRadians;
        double cos = Math.Cos(gast);
        double sin = Math.Sin(gast);
        return new Vec3((cos * trueOfDate.X) + (sin * trueOfDate.Y), (-sin * trueOfDate.X) + (cos * trueOfDate.Y), trueOfDate.Z);
    }
}
