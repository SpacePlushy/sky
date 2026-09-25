using System.Globalization;
using Sky.Orbital.Elements;

namespace Sky.Orbital.Tests.Elements;

public class TleTests
{
    // Vallado verification case 00005 (Vanguard 1).
    private const string Line1 = "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753";
    private const string Line2 = "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667";

    [Fact]
    public void Parses_every_element_of_a_standard_tle()
    {
        var elements = Tle.Parse(Line1, Line2);

        Assert.Equal(5, elements.CatalogNumber);
        Assert.Equal(34.2682, elements.Inclination);
        Assert.Equal(348.7242, elements.RightAscensionOfAscendingNode);
        Assert.Equal(0.1859667, elements.Eccentricity);
        Assert.Equal(331.7664, elements.ArgumentOfPericenter);
        Assert.Equal(19.3264, elements.MeanAnomaly);
        Assert.Equal(10.82419157, elements.MeanMotion);
        Assert.Equal(2.8098e-5, elements.BStar);
        Assert.Equal(2.3e-7, elements.MeanMotionDot);
        Assert.Equal(0.0, elements.MeanMotionDdot);
    }

    [Fact]
    public void Converts_day_of_year_epoch_to_exact_utc_instant()
    {
        // Day 179 of leap year 2000 is June 27. 0.78495062 day = 67819.733568 s = 18:50:19.733568.
        var expected = new DateTimeOffset(2000, 6, 27, 18, 50, 19, TimeSpan.Zero).AddTicks(7_335_680);

        var elements = Tle.Parse(Line1, Line2);

        Assert.Equal(expected, elements.Epoch);
    }

    [Fact]
    public void Treats_two_digit_years_from_57_as_1900s()
    {
        // Vallado case 11801: day 230 of leap year 1980 is August 17. 0.29629788 day = 07:06:40.136832.
        var elements = Tle.Parse(
            "1 11801U          80230.29629788  .01431103  00000-0  14311-1      13",
            "2 11801  46.7916 230.4354 7318036  47.4722  10.4117  2.28537848    13");

        var expected = new DateTimeOffset(1980, 8, 17, 7, 6, 40, TimeSpan.Zero).AddTicks(1_368_320);
        Assert.Equal(expected, elements.Epoch);
    }

    [Theory]
    [InlineData("56", 2056)]
    [InlineData("57", 1957)]
    [InlineData("00", 2000)]
    [InlineData("99", 1999)]
    public void Two_digit_years_pivot_between_56_and_57(string year, int expected)
    {
        var line1 = "1 00005U 58002B   " + year + "179.78495062  .00000023  00000-0  28098-4 0  4753";

        Assert.Equal(expected, Tle.Parse(line1, Line2).Epoch.Year);
    }

    [Theory]
    [InlineData("1 00005U 58002B   00    NaN     .00000023  00000-0  28098-4 0  4753", Line2)] // epoch day
    [InlineData("1 00005U 58002B   00367.50000000  .00000023  00000-0  28098-4 0  4753", Line2)] // day past year end
    [InlineData("1 00005U 58002B   00000.50000000  .00000023  00000-0  28098-4 0  4753", Line2)] // day zero
    [InlineData(Line1, "2 00005      NaN 348.7242 1859667 331.7664  19.3264 10.82419157413667")] // inclination
    [InlineData(Line1, "2 00005  34.2682 Infinity 1859667 331.7664  19.3264 10.82419157413667")] // RAAN
    [InlineData(Line1, "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 1.0e+0001413667")]  // exponent form
    public void Rejects_non_finite_or_out_of_range_values(string line1, string line2)
    {
        Assert.Throws<FormatException>(() => Tle.Parse(line1, line2));
    }

    [Theory]
    // Fields use an implied leading decimal point and a signed power-of-ten exponent.
    [InlineData("1 21897U 92011A   06176.02341244 -.00001273  00000-0 -13525-3 0  3044", -1.3525e-4)]
    [InlineData("1 29141U 85108AA  06170.26783845  .99999999  00000-0  13519-0 0   718", 0.13519)]
    [InlineData("1 20413U 83020D   05363.79166667  .00000000  00000-0  00000+0 0  7041", 0.0)]
    [InlineData("1 11801U          80230.29629788  .01431103  00000-0  14311-1      13", 0.014311)]
    public void Decodes_bstar_exponent_notation(string line1, double expectedBStar)
    {
        var line2 = "2 " + line1[2..7] + "  46.7916 230.4354 7318036  47.4722  10.4117  2.28537848    13";

        var elements = Tle.Parse(line1, line2);

        Assert.Equal(expectedBStar, elements.BStar);
    }

    [Fact]
    public void Decodes_negative_second_derivative_of_mean_motion()
    {
        var elements = Tle.Parse(
            "1 16925U 86065D   06151.67415771  .02550794 -30915-6  18784-3 0  4486",
            "2 16925  62.0906 295.0239 5596327 245.1593  47.9690  4.88511875148616");

        Assert.Equal(-3.0915e-7, elements.MeanMotionDdot);
        Assert.Equal(0.02550794, elements.MeanMotionDot);
    }

    [Fact]
    public void Parses_identically_under_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var elements = Tle.Parse(Line1, Line2);

            Assert.Equal(34.2682, elements.Inclination);
            Assert.Equal(10.82419157, elements.MeanMotion);
            Assert.Equal(2.8098e-5, elements.BStar);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(Line2, Line1)] // lines swapped
    [InlineData(Line1, "2 00006  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667")] // catalog numbers differ
    [InlineData("1 00005U 58002B   00179.78495062", Line2)] // line 1 truncated
    [InlineData(Line1, "2 00005  34.2682 348.7242 1859667 331.7664  19.3264")] // line 2 truncated
    [InlineData(Line1, "2 00005  34.2682 348.7242 18596X7 331.7664  19.3264 10.82419157413667")] // bad digit
    public void Rejects_malformed_input(string line1, string line2)
    {
        Assert.Throws<FormatException>(() => Tle.Parse(line1, line2));
    }
}
