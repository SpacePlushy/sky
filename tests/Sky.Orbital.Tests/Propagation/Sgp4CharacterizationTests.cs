using Sky.Orbital.Propagation;

namespace Sky.Orbital.Tests.Propagation;

/// <summary>
/// Documents measured properties of SGP4 itself that downstream accuracy depends on.
/// These bounds are measurements, not tolerances derived from analysis, and each one was
/// reproduced with an independent implementation before being recorded.
/// </summary>
public class Sgp4CharacterizationTests
{
    [Fact]
    public void Sgp4_velocity_matches_the_derivative_of_sgp4_position_within_3_cm_per_second()
    {
        // SGP4 computes velocity with approximate short-period terms, so its velocity is not
        // exactly the time derivative of its position. For this ISS element set over 7 days the gap
        // peaks at 2.24e-5 km/s (22 mm/s), identical in python-sgp4 2.25 and 2.27
        // (pure Python and compiled C++). Range rate inherits this.
        // A unit or scaling error in the wrapper would break the bound by orders of magnitude.
        var iss = TestElements.Iss20260924;
        var propagator = Sgp4Propagator.Create(iss);
        var random = new Random(42);
        const double h = 1.0 / 60.0; // 1 s, in minutes

        double worstKmPerSecond = 0;
        for (int i = 0; i < 500; i++)
        {
            double t = random.NextDouble() * 7 * 1440;
            Vec3 P(double minutes) => propagator.Propagate(minutes).State.Position;

            var p2 = P(t + (2 * h));
            var p1 = P(t + h);
            var m1 = P(t - h);
            var m2 = P(t - (2 * h));
            double scale = 12 * h * 60; // fourth-order stencil denominator, converted to seconds
            var numeric = new Vec3(
                (-p2.X + (8 * p1.X) - (8 * m1.X) + m2.X) / scale,
                (-p2.Y + (8 * p1.Y) - (8 * m1.Y) + m2.Y) / scale,
                (-p2.Z + (8 * p1.Z) - (8 * m1.Z) + m2.Z) / scale);
            var velocity = propagator.Propagate(t).State.Velocity;

            double gap = Math.Sqrt(
                Math.Pow(numeric.X - velocity.X, 2) + Math.Pow(numeric.Y - velocity.Y, 2) + Math.Pow(numeric.Z - velocity.Z, 2));
            worstKmPerSecond = Math.Max(worstKmPerSecond, gap);
        }

        Assert.InRange(worstKmPerSecond, 1e-6, 3e-5);
    }
}
