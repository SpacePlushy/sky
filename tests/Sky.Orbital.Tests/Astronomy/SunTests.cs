using Sky.Orbital.Astronomy;
using Sky.Orbital.Time;

namespace Sky.Orbital.Tests.Astronomy;

public class SunTests
{
    [Fact]
    public void Matches_meeus_example_25a()
    {
        // J. Meeus, Astronomical Algorithms, 2nd ed., Example 25.a: 1992 October 13.0 TD,
        // JDE 2448908.5, by the low-accuracy method of chapter 25. Published results:
        // apparent right ascension 198.38083°, apparent declination −7.78507°, radius vector
        // 0.99766 AU. Sky runs the same formulas, so it must reproduce the printed digits: the
        // tolerance is half a unit in the last printed place plus rounding.
        SunPlace place = Sun.Apparent(new JulianDate(2448908.5, 0.0));

        Assert.Equal(198.38083, place.RightAscensionDegrees, 1e-5);
        Assert.Equal(-7.78507, place.DeclinationDegrees, 1e-5);
        Assert.Equal(0.99766, place.DistanceAu, 1e-5);
    }

    [Fact]
    public void Sidereal_time_matches_meeus_example_12a()
    {
        // Meeus, Example 12.a: 1987 April 10, 0h UT (JD 2446895.5). Mean sidereal time at Greenwich,
        // by the same IAU-82 formula Sky uses, is 13h10m46.3668s, printed to 1e-4 s. Apparent sidereal
        // time with the full nutation (Δψ = −3.788″, ε = 23°26′36.85″) is 13h10m46.1351s. Sky's
        // one-term nutation leaves out terms worth at most 2.1″ in the equation of the equinoxes,
        // 0.14 s of time, so Sky's apparent sidereal time must be within that of the book's.
        var jd = new JulianDate(2446895.5, 0.0);
        double meanSeconds = SiderealTime.GreenwichMean(jd) / (2 * Math.PI) * 86400.0;
        double apparentSeconds = (SiderealTime.GreenwichMean(jd) + Sun.Apparent(jd).EquationOfEquinoxesRadians) / (2 * Math.PI) * 86400.0;

        Assert.Equal((13 * 3600) + (10 * 60) + 46.3668, meanSeconds, 1e-4);
        Assert.Equal((13 * 3600) + (10 * 60) + 46.1351, apparentSeconds, 0.14);
    }

    [Fact]
    public void Declination_and_distance_stay_within_their_physical_ranges()
    {
        // Seeded random instants, 1950 to 2050, the span Meeus's method is meant for. The Sun's
        // declination never exceeds the obliquity (23.45° here, with nutation), and its distance
        // stays between perihelion and aphelion, 0.9833 and 1.0167 AU.
        var random = new Random(25);
        for (int i = 0; i < 5000; i++)
        {
            double days = (random.NextDouble() * 36525 * 2) - 36525 - 18262.5; // 1950 to 2050
            var jd = new JulianDate(2451544.5 + Math.Floor(days), days - Math.Floor(days));
            SunPlace place = Sun.Apparent(jd);

            Assert.InRange(place.DeclinationDegrees, -23.46, 23.46);
            Assert.InRange(place.DistanceAu, 0.9832, 1.0168);
            Assert.InRange(place.RightAscensionDegrees, 0.0, 360.0);
            Assert.True(place.RightAscensionDegrees < 360.0);
        }
    }

    [Fact]
    public void Earth_fixed_position_has_the_sun_distance_and_the_apparent_declination()
    {
        // The Earth-fixed vector is the apparent place rotated about the pole, so its length is
        // the distance and its latitude is the declination. Only the rotation angle is new.
        var random = new Random(26);
        for (int i = 0; i < 2000; i++)
        {
            var instant = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.NextDouble() * 50 * 365.25 * 86400);
            SunPlace place = Sun.Apparent(JulianDate.FromInstant(instant));
            Vec3 ecef = Sun.PositionEcef(instant);

            Assert.Equal(place.DistanceAu * Sun.AstronomicalUnitKm, ecef.Length, 1e-6);
            double latitude = Math.Asin(ecef.Z / ecef.Length) * 180.0 / Math.PI;
            Assert.Equal(place.DeclinationDegrees, latitude, 1e-10);
        }
    }

    [Fact]
    public void The_sun_crosses_the_local_meridian_near_local_apparent_noon()
    {
        // Where the Sun is overhead in longitude is set by the Earth's rotation and the equation of
        // time, which stays within ±16.5 minutes (±4.2°). At 12:00 UTC the subsolar longitude is
        // therefore within 4.2° of 0°, on every day of the year. This checks the rotation's sign
        // and origin independently of any reference data.
        for (int day = 0; day < 366; day++)
        {
            var noon = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(day);
            Vec3 sun = Sun.PositionEcef(noon);
            double longitude = Math.Atan2(sun.Y, sun.X) * 180.0 / Math.PI;
            Assert.InRange(longitude, -4.2, 4.2);
        }
    }
}
