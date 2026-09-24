namespace Sky.Orbital.Time;

/// <summary>Sidereal time: the Earth's rotation angle relative to the mean equinox.</summary>
/// <remarks>
/// IAU-82 Greenwich mean sidereal time is the model SGP4's TEME frame is defined against
/// (Vallado et al. 2006, AIAA 2006-6753), so it is the correct rotation from TEME to Earth-fixed:
/// GMST(s) = 67310.54841 + (876600 h + 8640184.812866 s) T + 0.093104 T² - 6.2e-6 T³,
/// with T in Julian centuries of UT1 since J2000.0.
/// </remarks>
public static class SiderealTime
{
    private const double TwoPi = 2.0 * Math.PI;
    private const double J2000 = 2451545.0;
    private const double SecondsPerDay = 86400.0;
    private const double DaysPerCentury = 36525.0;

    /// <summary>Greenwich mean sidereal time (IAU-82), in radians in [0, 2π).</summary>
    public static double GreenwichMean(JulianDate ut1)
    {
        double t = ut1.DaysSinceJ2000 / DaysPerCentury;

        // The 876600 h term is 3,155,760,000 s per century, which is 86400 s of sidereal time
        // per UT1 day: exactly one turn per day. It therefore contributes only the fraction of
        // the current day, taken here from the split Julian date without rounding. Evaluating the
        // term as seconds and reducing modulo 2π instead would leave about 5e-11 rad of rounding
        // noise, about 0.3 mm at low-Earth-orbit radius.
        double wholeDays = ut1.Whole - J2000; // exact: both are multiples of 0.5 well inside double range
        double dayTurns = (wholeDays - Math.Floor(wholeDays)) + ut1.Fraction;

        double remainingSeconds = 67310.54841
            + (8640184.812866 * t)
            + (0.093104 * t * t)
            - (6.2e-6 * t * t * t);

        double turns = dayTurns + (remainingSeconds / SecondsPerDay);
        return (turns - Math.Floor(turns)) * TwoPi;
    }

    /// <summary>
    /// Rate of change of Greenwich mean sidereal time, in radians per UT1 second: the exact
    /// derivative of <see cref="GreenwichMean"/>.
    /// </summary>
    /// <remarks>
    /// About 7.2921158553e-5 rad/s. This exceeds the Earth's inertial rotation rate
    /// (7.29211514670698e-5 rad/s) by the precession of the mean equinox, 7.1e-12 rad/s.
    /// </remarks>
    public static double GreenwichMeanRate(JulianDate ut1)
    {
        double t = ut1.DaysSinceJ2000 / DaysPerCentury;
        double siderealSecondsPerCentury = ((876600.0 * 3600.0) + 8640184.812866)
            + (2.0 * 0.093104 * t)
            - (3.0 * 6.2e-6 * t * t);
        double siderealSecondsPerUt1Second = siderealSecondsPerCentury / (DaysPerCentury * SecondsPerDay);
        return siderealSecondsPerUt1Second * TwoPi / SecondsPerDay;
    }
}
