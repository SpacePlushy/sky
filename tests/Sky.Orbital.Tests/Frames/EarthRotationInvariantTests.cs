using Sky.Orbital.Frames;
using Sky.Orbital.Propagation;
using Sky.Orbital.Time;

namespace Sky.Orbital.Tests.Frames;

/// <summary>
/// Properties TEME to Earth-fixed must satisfy for every input, checked over many seeded random
/// inputs so every run uses the same values. These catch errors a single worked example can miss.
/// </summary>
public class EarthRotationInvariantTests
{
    private const int Samples = 2000;
    private static readonly DateTimeOffset Start = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rotation_preserves_length_and_the_polar_component()
    {
        // TEME to Earth-fixed is a rotation about the z axis, so it cannot change |r| or z.
        var random = new Random(20260924);
        for (int i = 0; i < Samples; i++)
        {
            var teme = new TemeState(RandomVector(random, 30_000), RandomVector(random, 10));

            var ecef = EarthRotation.TemeToEcef(teme, RandomInstant(random));

            Assert.Equal(Length(teme.Position), Length(ecef.Position), 1e-9);
            Assert.Equal(teme.Position.Z, ecef.Position.Z);
        }
    }

    [Fact]
    public void Earth_fixed_velocity_is_the_exact_time_derivative_of_earth_fixed_position()
    {
        // A point moving in a straight line in TEME, r(t) = r0 + v0 t, has TEME velocity exactly v0.
        // Differentiating its Earth-fixed position numerically must reproduce the analytic
        // Earth-fixed velocity, including the sign and size of the Earth-rotation term. A
        // fourth-order central difference with h = 1 s has no truncation error worth counting for
        // this motion, and rounding contributes under 4e-9 km/s at 50,000 km, so the tolerance
        // is 1e-8 km/s (0.01 mm/s). Using any rotation rate other than the rate of the GMST angle
        // itself would show up here: Vallado's inertial rate is off by 7.1e-12 rad/s, or 3.5e-7 km/s
        // at 50,000 km from the axis.
        var random = new Random(42);
        const double h = 1.0;
        for (int i = 0; i < Samples; i++)
        {
            var r0 = RandomVector(random, 30_000);
            var v0 = RandomVector(random, 10);
            var t = RandomInstant(random);

            Vec3 PositionAt(double seconds) => EarthRotation.TemeToEcef(
                new TemeState(new Vec3(r0.X + (v0.X * seconds), r0.Y + (v0.Y * seconds), r0.Z + (v0.Z * seconds)), v0),
                t.AddTicks((long)(seconds * TimeSpan.TicksPerSecond))).Position;

            var p2 = PositionAt(2 * h);
            var p1 = PositionAt(h);
            var m1 = PositionAt(-h);
            var m2 = PositionAt(-2 * h);
            var numeric = new Vec3(
                (-p2.X + (8 * p1.X) - (8 * m1.X) + m2.X) / (12 * h),
                (-p2.Y + (8 * p1.Y) - (8 * m1.Y) + m2.Y) / (12 * h),
                (-p2.Z + (8 * p1.Z) - (8 * m1.Z) + m2.Z) / (12 * h));

            var analytic = EarthRotation.TemeToEcef(new TemeState(r0, v0), t).Velocity;

            Assert.True(
                Length(Subtract(numeric, analytic)) < 1e-8,
                $"At {t:O}, r0 {r0}: numeric {numeric}, analytic {analytic}, difference {Length(Subtract(numeric, analytic)):E2} km/s.");
        }
    }

    [Fact]
    public void A_point_at_rest_in_teme_moves_at_the_gmst_rate_times_its_distance_from_the_axis()
    {
        var random = new Random(7);
        for (int i = 0; i < Samples; i++)
        {
            var position = RandomVector(random, 30_000);
            var instant = RandomInstant(random);

            var ecef = EarthRotation.TemeToEcef(new TemeState(position, default), instant);

            double rho = Math.Sqrt((position.X * position.X) + (position.Y * position.Y));
            double rate = SiderealTime.GreenwichMeanRate(JulianDate.FromInstant(instant));
            Assert.Equal(rate * rho, Length(ecef.Velocity), 1e-12);
            Assert.Equal(0.0, ecef.Velocity.Z);
        }
    }

    private static Vec3 RandomVector(Random random, double scale) => new(
        ((random.NextDouble() * 2) - 1) * scale,
        ((random.NextDouble() * 2) - 1) * scale,
        ((random.NextDouble() * 2) - 1) * scale);

    private static DateTimeOffset RandomInstant(Random random) =>
        Start.AddTicks((long)(random.NextDouble() * 100 * 365.25 * TimeSpan.TicksPerDay));

    private static Vec3 Subtract(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static double Length(Vec3 v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
}
