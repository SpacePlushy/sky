namespace Sky.Orbital.Time;

/// <summary>Sidereal time: the Earth's rotation angle relative to the equinox.</summary>
public static class SiderealTime
{
    private const double TwoPi = 2.0 * Math.PI;
    private const double RadiansPerSecondOfTime = TwoPi / 86400.0;

    /// <summary>
    /// Greenwich mean sidereal time from the IAU-82 model, in radians in [0, 2π).
    /// </summary>
    /// <remarks>
    /// This is the model SGP4's TEME frame is defined against, so it is the correct rotation
    /// from TEME to Earth-fixed coordinates (Vallado et al. 2006, AIAA 2006-6753, section 7):
    /// GMST(s) = 67310.54841 + (876600 h + 8640184.812866 s) T + 0.093104 T² - 6.2e-6 T³,
    /// with T in Julian centuries of UT1 since J2000.0.
    /// </remarks>
    public static double GreenwichMean(JulianDate ut1)
    {
        double t = ut1.DaysSinceJ2000 / 36525.0;
        double seconds = 67310.54841
            + (((876600.0 * 3600.0) + 8640184.812866) * t)
            + (0.093104 * t * t)
            - (6.2e-6 * t * t * t);

        double radians = (seconds * RadiansPerSecondOfTime) % TwoPi;
        return radians < 0.0 ? radians + TwoPi : radians;
    }
}
