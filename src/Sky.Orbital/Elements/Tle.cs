using System.Globalization;

namespace Sky.Orbital.Elements;

/// <summary>
/// Reads two-line element sets (TLEs).
/// </summary>
/// <remarks>
/// Live data comes from OMM, not TLEs, because TLEs cannot hold catalog numbers
/// above 99999. This parser exists to read Vallado's SGP4 verification files and
/// other historical data. Column positions follow the format definition at
/// https://celestrak.org/NORAD/documentation/tle-fmt.php. All numbers are parsed
/// with the invariant culture, so results do not depend on the machine's locale.
/// </remarks>
public static class Tle
{
    private const int LineLength = 69;

    /// <summary>Parses the two lines of a TLE into mean elements.</summary>
    /// <exception cref="FormatException">The lines are not a valid TLE.</exception>
    public static MeanElements Parse(string line1, string line2)
    {
        ArgumentNullException.ThrowIfNull(line1);
        ArgumentNullException.ThrowIfNull(line2);
        RequireLine(line1, '1');
        RequireLine(line2, '2');

        long catalogNumber = ParseLong(line1, 3, 7, "catalog number");
        if (ParseLong(line2, 3, 7, "catalog number") != catalogNumber)
        {
            throw new FormatException("TLE lines 1 and 2 have different catalog numbers.");
        }

        return new MeanElements
        {
            CatalogNumber = catalogNumber,
            Epoch = ParseEpoch(line1),
            MeanMotionDot = ParseDouble(Field(line1, 34, 43), "mean motion first derivative"),
            MeanMotionDdot = ParseImpliedExponent(line1, 45, "mean motion second derivative"),
            BStar = ParseImpliedExponent(line1, 54, "B*"),
            Inclination = ParseDouble(Field(line2, 9, 16), "inclination"),
            RightAscensionOfAscendingNode = ParseDouble(Field(line2, 18, 25), "right ascension of ascending node"),
            Eccentricity = ParseDouble("0." + Field(line2, 27, 33), "eccentricity"),
            ArgumentOfPericenter = ParseDouble(Field(line2, 35, 42), "argument of pericenter"),
            MeanAnomaly = ParseDouble(Field(line2, 44, 51), "mean anomaly"),
            MeanMotion = ParseDouble(Field(line2, 53, 63), "mean motion"),
        };
    }

    private static void RequireLine(string line, char lineNumber)
    {
        if (line.Length < LineLength || line[0] != lineNumber || line[1] != ' ')
        {
            throw new FormatException(
                $"TLE line {lineNumber} must start with '{lineNumber} ' and be at least {LineLength} characters long.");
        }
    }

    /// <summary>Epoch year in columns 19-20 and day of year with fraction in columns 21-32.</summary>
    private static DateTimeOffset ParseEpoch(string line1)
    {
        int twoDigitYear = (int)ParseLong(line1, 19, 20, "epoch year");
        double dayOfYear = ParseDouble(Field(line1, 21, 32), "epoch day");

        // TLE convention: years 57-99 are 1957-1999, years 00-56 are 2000-2056.
        int year = twoDigitYear < 57 ? 2000 + twoDigitYear : 1900 + twoDigitYear;
        long ticks = (long)Math.Round((dayOfYear - 1.0) * TimeSpan.TicksPerDay);
        return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks);
    }

    /// <summary>
    /// Reads an eight-column field such as " 28098-4": a sign, five mantissa digits with
    /// an implied leading decimal point, and a signed one-digit power of ten.
    /// " 28098-4" means +0.28098e-4.
    /// </summary>
    private static double ParseImpliedExponent(string line, int firstColumn, string name)
    {
        string field = Field(line, firstColumn, firstColumn + 7);
        char mantissaSign = SignOf(field[0], name);
        char exponentSign = SignOf(field[6], name);
        string text = $"{mantissaSign}0.{field.Substring(1, 5)}e{exponentSign}{field[7]}";
        return ParseDouble(text, name);
    }

    private static char SignOf(char c, string name) => c switch
    {
        ' ' or '+' => '+',
        '-' => '-',
        _ => throw new FormatException($"TLE {name} has an invalid sign character '{c}'."),
    };

    /// <summary>Returns the text in 1-based, inclusive columns, as the format definition numbers them.</summary>
    private static string Field(string line, int firstColumn, int lastColumn) =>
        line.Substring(firstColumn - 1, lastColumn - firstColumn + 1);

    private static long ParseLong(string line, int firstColumn, int lastColumn, string name)
    {
        string text = Field(line, firstColumn, lastColumn);
        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new FormatException($"TLE {name} '{text}' is not a whole number.");
    }

    private static double ParseDouble(string text, string name) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new FormatException($"TLE {name} '{text}' is not a number.");
}
