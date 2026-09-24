namespace Sky.Orbital.Time;

/// <summary>
/// A Julian date split into the Julian date of the preceding midnight (<see cref="Whole"/>,
/// always ending in .5) and the fraction of the day since then.
/// </summary>
/// <remarks>
/// A single double near Julian date 2.46 million resolves only about 40 microseconds, which
/// is about 2 cm of Earth rotation at the ISS's radius. Keeping the parts separate preserves
/// the full resolution of the instant. The time scale is whatever the instant represents:
/// Sky treats UTC as UT1 (see docs/verification.md, assumption A5).
/// </remarks>
public readonly record struct JulianDate(double Whole, double Fraction)
{
    private const double J2000 = 2451545.0;
    private const double JulianDateOfJ2000Midnight = 2451544.5;
    private static readonly DateTime J2000Midnight = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The Julian date of the given instant, measured in UTC.</summary>
    public static JulianDate FromInstant(DateTimeOffset instant)
    {
        DateTime utc = instant.UtcDateTime;
        DateTime midnight = utc.Date;
        double whole = JulianDateOfJ2000Midnight + (midnight - J2000Midnight).Days;
        double fraction = (utc - midnight).Ticks / (double)TimeSpan.TicksPerDay;
        return new JulianDate(whole, fraction);
    }

    /// <summary>The Julian date as a single number. Loses precision; prefer <see cref="DaysSinceJ2000"/>.</summary>
    public double Value => Whole + Fraction;

    /// <summary>Days since J2000.0 (2000 January 1, 12:00), at full precision.</summary>
    public double DaysSinceJ2000 => (Whole - J2000) + Fraction;
}
