using Sky.Orbital.Frames;

namespace Sky.Orbital.Tests.Frames;

/// <summary>
/// Look angles from an observer. Expected values come from exact geometry, not from the code
/// under test: the east/north/up axes are written out here from their textbook definitions.
/// </summary>
public class TopocentricFrameTests
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private static readonly Geodetic Phoenix = new(33.4478, -112.0972, 0.331);

    [Fact]
    public void A_satellite_straight_overhead_is_at_90_degrees_and_its_height_difference_away()
    {
        var frame = new TopocentricFrame(Phoenix);
        var satellite = Wgs84.ToEcef(Phoenix with { HeightKm = Phoenix.HeightKm + 420.0 });

        var look = frame.LookAt(new EcefState(satellite, default));

        Assert.Equal(90.0, look.ElevationDegrees, 1e-9);
        Assert.Equal(420.0, look.RangeKm, 1e-9);
        Assert.Equal(0.0, look.RangeRateKmPerSecond);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(45.0, 10.0)]
    [InlineData(90.0, 45.0)]
    [InlineData(135.0, 80.0)]
    [InlineData(180.0, 30.0)]
    [InlineData(225.0, 5.0)]
    [InlineData(270.0, 60.0)]
    [InlineData(315.0, 89.0)]
    [InlineData(359.5, -5.0)]
    public void Recovers_a_satellite_placed_at_a_known_azimuth_and_elevation(double azimuth, double elevation)
    {
        var random = new Random((int)(azimuth * 10) + (int)(elevation * 1000));
        for (int i = 0; i < 200; i++)
        {
            var observer = new Geodetic((random.NextDouble() * 178) - 89, (random.NextDouble() * 360) - 180, random.NextDouble() * 3);
            double range = 300 + (random.NextDouble() * 40_000);
            var (east, north, up) = LocalAxes(observer);
            double az = azimuth * DegreesToRadians;
            double el = elevation * DegreesToRadians;
            var lineOfSight = Add(
                Add(Scale(east, Math.Cos(el) * Math.Sin(az)), Scale(north, Math.Cos(el) * Math.Cos(az))),
                Scale(up, Math.Sin(el)));
            var satellite = Add(Wgs84.ToEcef(observer), Scale(lineOfSight, range));

            var look = new TopocentricFrame(observer).LookAt(new EcefState(satellite, default));

            Assert.Equal(0.0, AngleDifference(azimuth, look.AzimuthDegrees), 1e-9);
            Assert.Equal(elevation, look.ElevationDegrees, 1e-9);
            Assert.Equal(range, look.RangeKm, 1e-8);
        }
    }

    [Fact]
    public void A_satellite_due_north_on_the_same_meridian_is_at_azimuth_zero()
    {
        // The observer's meridian plane contains its north and up axes, so any point in that plane
        // north of the observer has no east component. No axis formula is needed to know this.
        foreach (double lat in new[] { -60.0, -20.0, 0.0, 33.4478, 70.0 })
        {
            var observer = new Geodetic(lat, -112.0972, 0.331);
            var satellite = Wgs84.ToEcef(new Geodetic(lat + 3.0, -112.0972, 420.0));

            var look = new TopocentricFrame(observer).LookAt(new EcefState(satellite, default));

            Assert.True(
                Math.Min(look.AzimuthDegrees, 360.0 - look.AzimuthDegrees) < 1e-9,
                $"Latitude {lat}: azimuth {look.AzimuthDegrees}.");
            Assert.True(look.ElevationDegrees > 0);
        }
    }

    [Fact]
    public void Satellites_east_and_west_at_equal_offsets_mirror_each_other()
    {
        // Reflection through the observer's meridian plane swaps east and west, so the azimuths
        // sum to 360 degrees and elevation and range are unchanged.
        var frame = new TopocentricFrame(Phoenix);
        foreach (double offset in new[] { 0.5, 5.0, 15.0 })
        {
            var east = frame.LookAt(new EcefState(Wgs84.ToEcef(new Geodetic(Phoenix.LatitudeDegrees + 1, Phoenix.LongitudeDegrees + offset, 800)), default));
            var west = frame.LookAt(new EcefState(Wgs84.ToEcef(new Geodetic(Phoenix.LatitudeDegrees + 1, Phoenix.LongitudeDegrees - offset, 800)), default));

            Assert.Equal(360.0, east.AzimuthDegrees + west.AzimuthDegrees, 1e-9);
            Assert.InRange(east.AzimuthDegrees, 0.0, 180.0);
            Assert.Equal(east.ElevationDegrees, west.ElevationDegrees, 1e-9);
            Assert.Equal(east.RangeKm, west.RangeKm, 1e-9);
        }
    }

    [Fact]
    public void A_satellite_on_the_far_side_of_the_earth_is_below_the_horizon()
    {
        var antipode = new Geodetic(-Phoenix.LatitudeDegrees, Phoenix.LongitudeDegrees + 180.0, 420.0);

        var look = new TopocentricFrame(Phoenix).LookAt(new EcefState(Wgs84.ToEcef(antipode), default));

        Assert.True(look.ElevationDegrees < -80.0, $"Elevation {look.ElevationDegrees}.");
    }

    [Fact]
    public void Range_rate_is_positive_when_receding_and_negative_when_approaching()
    {
        var frame = new TopocentricFrame(Phoenix);
        var observer = Wgs84.ToEcef(Phoenix);
        var satellite = Wgs84.ToEcef(Phoenix with { HeightKm = 1000.0 });
        var outward = Scale(Subtract(satellite, observer), 1.0 / Length(Subtract(satellite, observer)));

        Assert.Equal(7.0, frame.LookAt(new EcefState(satellite, Scale(outward, 7.0))).RangeRateKmPerSecond, 1e-12);
        Assert.Equal(-7.0, frame.LookAt(new EcefState(satellite, Scale(outward, -7.0))).RangeRateKmPerSecond, 1e-12);

        var sideways = new Vec3(-outward.Y, outward.X, 0); // perpendicular to the line of sight
        Assert.Equal(0.0, frame.LookAt(new EcefState(satellite, sideways)).RangeRateKmPerSecond, 1e-12);
    }

    [Fact]
    public void Range_rate_is_the_exact_time_derivative_of_range()
    {
        // Straight-line motion in the Earth-fixed frame, so the true velocity is known exactly.
        // Error budget for the fourth-order central difference, measured over 20,000 samples with
        // ranges from 300 km and speeds up to 14 km/s: h = 1 s gives 8.4e-6 km/s (range curves
        // sharply at close range, so truncation dominates and falls as h^4), h = 0.05 s gives
        // 4.5e-11, and h = 0.001 s gives 1.5e-9 (rounding dominates and grows as 1/h). With
        // h = 0.05 s the 1e-9 km/s tolerance has a 20x margin.
        var random = new Random(9);
        const double h = 0.05;
        for (int i = 0; i < 2000; i++)
        {
            var observer = new Geodetic((random.NextDouble() * 178) - 89, (random.NextDouble() * 360) - 180, random.NextDouble() * 3);
            var frame = new TopocentricFrame(observer);
            var r0 = Wgs84.ToEcef(observer with { HeightKm = 300 + (random.NextDouble() * 2000) });
            var v0 = new Vec3((random.NextDouble() * 16) - 8, (random.NextDouble() * 16) - 8, (random.NextDouble() * 16) - 8);

            double RangeAt(double t) => frame.LookAt(new EcefState(Add(r0, Scale(v0, t)), v0)).RangeKm;
            double numeric = (-RangeAt(2 * h) + (8 * RangeAt(h)) - (8 * RangeAt(-h)) + RangeAt(-2 * h)) / (12 * h);

            Assert.Equal(numeric, frame.LookAt(new EcefState(r0, v0)).RangeRateKmPerSecond, 1e-9);
        }
    }

    [Fact]
    public void Azimuth_stays_below_360_when_the_satellite_is_a_hair_west_of_north()
    {
        // atan2 of a tiny negative east component is a tiny negative angle, and adding 360 to it
        // rounds to exactly 360.0 in double precision. The documented range is [0, 360).
        var frame = new TopocentricFrame(Phoenix);
        var (east, north, _) = LocalAxes(Phoenix);
        var satellite = Wgs84.ToEcef(Phoenix) + (north * 1000.0) + (east * -1e-14);

        double azimuth = frame.LookAt(new EcefState(satellite, default)).AzimuthDegrees;

        Assert.InRange(azimuth, 0.0, 359.999999);
        Assert.Equal(0.0, AngleDifference(0.0, azimuth), 1e-9);
    }

    /// <summary>Signed difference b - a between two angles in degrees, in [-180, 180).</summary>
    private static double AngleDifference(double a, double b) => ((((b - a) % 360.0) + 540.0) % 360.0) - 180.0;

    /// <summary>East, north, and up unit vectors at a geodetic position, from their definitions.</summary>
    private static (Vec3 East, Vec3 North, Vec3 Up) LocalAxes(Geodetic g)
    {
        double lat = g.LatitudeDegrees * DegreesToRadians;
        double lon = g.LongitudeDegrees * DegreesToRadians;
        var east = new Vec3(-Math.Sin(lon), Math.Cos(lon), 0);
        var north = new Vec3(-Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat));
        var up = new Vec3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        return (east, north, up);
    }

    private static Vec3 Add(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static Vec3 Subtract(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Vec3 Scale(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);

    private static double Length(Vec3 v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
}
