using Sky.Orbital.Frames;

namespace Sky.Orbital.Tests.Frames;

/// <summary>
/// WGS-84 geodetic coordinates. The first three tests check the defining properties of geodetic
/// coordinates directly, which together pin the conversion down uniquely. The rest check the
/// inverse, exact special cases, and a published example.
/// </summary>
public class GeodeticTests
{
    private const double A = 6378.137;                  // WGS-84 equatorial radius, km
    private const double InverseFlattening = 298.257223563;
    private static readonly double B = A * (1 - (1 / InverseFlattening)); // polar radius, 6356.752314245 km
    private const double DegreesToRadians = Math.PI / 180.0;

    private static readonly double[] Heights = [-0.5, 0.0, 0.331, 10.0, 420.0, 20_200.0, 35_786.0];

    [Fact]
    public void Points_at_zero_height_lie_on_the_wgs84_ellipsoid()
    {
        for (double lat = -90; lat <= 90; lat += 1)
        {
            for (double lon = -180; lon <= 180; lon += 7.5)
            {
                var r = Wgs84.ToEcef(new Geodetic(lat, lon, 0));

                double ellipsoid = ((r.X * r.X) + (r.Y * r.Y)) / (A * A) + (r.Z * r.Z / (B * B));
                Assert.Equal(1.0, ellipsoid, 1e-14);
            }
        }
    }

    [Fact]
    public void Height_is_measured_along_the_direction_set_by_latitude_and_longitude()
    {
        // Geodetic latitude and longitude define the unit vector n = (cos lat cos lon, cos lat sin lon, sin lat),
        // and height moves the point along n.
        foreach (double h in Heights)
        {
            for (double lat = -90; lat <= 90; lat += 5)
            {
                for (double lon = -180; lon <= 180; lon += 15)
                {
                    var surface = Wgs84.ToEcef(new Geodetic(lat, lon, 0));
                    var raised = Wgs84.ToEcef(new Geodetic(lat, lon, h));
                    var n = Normal(lat, lon);

                    Assert.Equal(surface.X + (h * n.X), raised.X, 1e-9);
                    Assert.Equal(surface.Y + (h * n.Y), raised.Y, 1e-9);
                    Assert.Equal(surface.Z + (h * n.Z), raised.Z, 1e-9);
                }
            }
        }
    }

    [Fact]
    public void That_direction_is_perpendicular_to_the_ellipsoid_surface()
    {
        // n must be the surface normal: perpendicular to both tangent directions, estimated with
        // central differences over 1e-4 degrees.
        const double step = 1e-4;
        for (double lat = -89; lat <= 89; lat += 2)
        {
            for (double lon = -180; lon <= 180; lon += 20)
            {
                var n = Normal(lat, lon);
                var north = Subtract(Wgs84.ToEcef(new Geodetic(lat + step, lon, 0)), Wgs84.ToEcef(new Geodetic(lat - step, lon, 0)));
                var east = Subtract(Wgs84.ToEcef(new Geodetic(lat, lon + step, 0)), Wgs84.ToEcef(new Geodetic(lat, lon - step, 0)));

                Assert.Equal(0.0, Dot(n, north) / Length(north), 1e-9);
                Assert.Equal(0.0, Dot(n, east) / Length(east), 1e-9);
            }
        }
    }

    [Fact]
    public void Equator_and_poles_have_exact_positions()
    {
        AssertClose(new Vec3(A, 0, 0), Wgs84.ToEcef(new Geodetic(0, 0, 0)), 1e-12);
        AssertClose(new Vec3(0, A + 1, 0), Wgs84.ToEcef(new Geodetic(0, 90, 1)), 1e-12);
        AssertClose(new Vec3(0, 0, B + 100), Wgs84.ToEcef(new Geodetic(90, 0, 100)), 1e-9);
        AssertClose(new Vec3(0, 0, -B), Wgs84.ToEcef(new Geodetic(-90, 0, 0)), 1e-9);
    }

    [Fact]
    public void Inverse_recovers_latitude_longitude_and_height_across_the_globe()
    {
        foreach (double h in Heights)
        {
            for (double lat = -90; lat <= 90; lat += 0.5)
            {
                for (double lon = -180; lon < 180; lon += 5)
                {
                    var g = Wgs84.FromEcef(Wgs84.ToEcef(new Geodetic(lat, lon, h)));

                    // Achieved: 2e-14 degrees and under 1e-10 km. Tolerances allow 100x that, so any
                    // approximate or iterative method with a loose threshold would fail here.
                    Assert.Equal(lat, g.LatitudeDegrees, 1e-12);
                    Assert.Equal(h, g.HeightKm, 1e-9); // 1 micrometer
                    if (Math.Abs(lat) < 90)
                    {
                        Assert.Equal(lon, g.LongitudeDegrees, 1e-12);
                    }
                }
            }
        }
    }

    [Fact]
    public void Forward_conversion_recovers_any_earth_fixed_point()
    {
        // The other direction: random points from 6,000 to 50,000 km from the center, any direction.
        var random = new Random(84);
        for (int i = 0; i < 20_000; i++)
        {
            double radius = 6000 + (random.NextDouble() * 44_000);
            double z = (random.NextDouble() * 2) - 1;
            double azimuth = random.NextDouble() * 2 * Math.PI;
            double s = Math.Sqrt(1 - (z * z));
            var r = new Vec3(radius * s * Math.Cos(azimuth), radius * s * Math.Sin(azimuth), radius * z);

            var back = Wgs84.ToEcef(Wgs84.FromEcef(r));

            // Achieved: 3.4e-11 km worst case. Tolerance 1e-9 km (1 micrometer).
            Assert.True(Length(Subtract(r, back)) < 1e-9, $"{r} came back as {back}.");
        }
    }

    [Fact]
    public void Poles_and_equator_invert_exactly()
    {
        var north = Wgs84.FromEcef(new Vec3(0, 0, B + 100));
        Assert.Equal(90.0, north.LatitudeDegrees, 1e-12);
        Assert.Equal(100.0, north.HeightKm, 1e-9);

        var south = Wgs84.FromEcef(new Vec3(0, 0, -(B + 400)));
        Assert.Equal(-90.0, south.LatitudeDegrees, 1e-12);
        Assert.Equal(400.0, south.HeightKm, 1e-9);

        var equator = Wgs84.FromEcef(new Vec3(-(A + 35_786), 0, 0));
        Assert.Equal(0.0, equator.LatitudeDegrees, 1e-12);
        Assert.Equal(180.0, Math.Abs(equator.LongitudeDegrees), 1e-12);
        Assert.Equal(35_786.0, equator.HeightKm, 1e-9);
    }

    [Fact]
    public void Matches_vallado_example_3_3()
    {
        // Vallado, Fundamentals of Astrodynamics and Applications, Example 3-3, on WGS-84,
        // at the precision of the reference values. Sky gives 34.352495151, 46.446416857, 5085.2187311.
        var g = Wgs84.FromEcef(new Vec3(6524.834, 6862.875, 6448.296));

        Assert.Equal(34.352495, g.LatitudeDegrees, 1e-6);
        Assert.Equal(46.446417, g.LongitudeDegrees, 1e-6);
        Assert.Equal(5085.2187, g.HeightKm, 1e-4);
    }

    [Fact]
    public void Rejects_points_near_the_center_of_the_earth()
    {
        // Geodetic coordinates are not unique within about a*e^2 = 42.7 km of the center.
        Assert.Throws<ArgumentOutOfRangeException>(() => Wgs84.FromEcef(new Vec3(10, 0, 0)));
    }

    private static Vec3 Normal(double latDegrees, double lonDegrees)
    {
        double lat = latDegrees * DegreesToRadians;
        double lon = lonDegrees * DegreesToRadians;
        return new Vec3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }

    private static Vec3 Subtract(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static double Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    private static double Length(Vec3 v) => Math.Sqrt(Dot(v, v));

    private static void AssertClose(Vec3 expected, Vec3 actual, double tolerance) =>
        Assert.True(Length(Subtract(expected, actual)) <= tolerance, $"Expected {expected}, got {actual}.");
}
