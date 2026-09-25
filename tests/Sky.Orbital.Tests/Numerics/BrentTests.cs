using Sky.Orbital.Numerics;

namespace Sky.Orbital.Tests.Numerics;

/// <summary>
/// Brent's root and minimum finders against functions whose answers are known exactly. The
/// guarantees under test are the ones the methods document: a root within the tolerance of a sign
/// change (plus 4·macheps·|x| of rounding), and a minimum within 2·(tolerance + √macheps·|x|).
/// </summary>
public class BrentTests
{
    private const double Tolerance = 1e-3;
    private const double MachineEpsilon = 1.1102230246251565e-16;

    public static TheoryData<string, double, double, double> Roots => new()
    {
        // name, a, b, exact root
        { "linear", -3.0, 7.0, 1.25 },
        { "cubic", 0.0, 4.0, Math.Cbrt(5.0) },
        { "sine", 2.0, 4.0, Math.PI },
        { "flat ninth power", -1.0, 3.0, 0.3 },
        { "steep exponential", -10.0, 10.0, 0.0 },
        { "root at an end", 2.0, 5.0, 2.0 },
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void Finds_known_roots_within_the_tolerance(string name, double a, double b, double root)
    {
        Func<double, double> f = name switch
        {
            "linear" => x => (2.0 * x) - 2.5,
            "cubic" => x => (x * x * x) - 5.0,
            "sine" => Math.Sin,
            // Nearly zero across a wide band around the root, which defeats interpolation and
            // forces Brent's bisection fallback.
            "flat ninth power" => x => Math.Pow(x - 0.3, 9),
            "steep exponential" => x => Math.Exp(x) - 1.0,
            "root at an end" => x => x - 2.0,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        var found = Brent.FindRoot(f, a, b, Tolerance);
        Assert.InRange(found.X, root - Bound(root), root + Bound(root));

        // The same bracket given the other way round.
        var reversed = Brent.FindRoot(f, b, a, Tolerance);
        Assert.InRange(reversed.X, root - Bound(root), root + Bound(root));

        static double Bound(double x) => Tolerance + (4 * MachineEpsilon * Math.Abs(x));
    }

    [Fact]
    public void Step_functions_are_located_at_their_jump_to_the_documented_bound()
    {
        // No interpolation can help with a jump, so Brent falls back to bisection, where its bound is
        // tight: a guarantee twice as loose as documented would fail here.
        var random = new Random(3);
        int nearBound = 0;
        for (int i = 0; i < 2000; i++)
        {
            double jump = random.NextDouble() * 10;
            double tolerance = Math.Pow(10, -1 - (random.NextDouble() * 4));
            foreach (var (a, b) in new[] { (0.0, 10.0), (10.0, 0.0) })
            {
                var found = Brent.FindRoot(x => x < jump ? -1.0 : 1.0, a, b, tolerance);
                double bound = tolerance + (4 * MachineEpsilon * Math.Abs(found.X));
                Assert.InRange(found.X, jump - bound, jump + bound);
                nearBound += Math.Abs(found.X - jump) > bound / 2 ? 1 : 0;
            }
        }

        // The bound is approached, so the test has teeth.
        Assert.True(nearBound > 100, $"Only {nearBound} results beyond half the bound.");
    }

    [Fact]
    public void Random_cubics_converge_within_the_tolerance_and_a_bounded_number_of_evaluations()
    {
        // Cubics with one real root in the bracket, at seeded random places and scales. Bisection
        // alone would need log2(width / tolerance) steps; Brent's worst case is about that squared.
        var random = new Random(2);
        for (int i = 0; i < 2000; i++)
        {
            double root = (random.NextDouble() * 200) - 100;
            double width = Math.Pow(10, (random.NextDouble() * 6) - 1);
            double a = root - (random.NextDouble() * width);
            double b = root + (random.NextDouble() * width);
            double k = (random.NextDouble() * 1.9) + 0.1;
            double tolerance = Math.Pow(10, -1 - (random.NextDouble() * 6));
            double F(double x) => (x - root) * (((x - root) * (x - root)) + k);

            var found = Brent.FindRoot(F, a, b, tolerance);

            double bound = tolerance + (4 * MachineEpsilon * Math.Abs(root));
            Assert.InRange(found.X, root - bound, root + bound);
            double bisections = Math.Ceiling(Math.Log2(Math.Max((b - a) / tolerance, 2)));
            Assert.True(found.Evaluations <= (bisections * bisections) + 3, $"{found.Evaluations} evaluations for {bisections} bisections");
        }
    }

    [Fact]
    public void Rejects_a_bracket_without_a_sign_change()
    {
        Assert.Throws<ArgumentException>(() => Brent.FindRoot(x => (x * x) + 1, -1, 1, Tolerance));
        Assert.Throws<ArgumentException>(() => Brent.FindRoot(x => x, double.NaN, 1, Tolerance));
        Assert.Throws<ArgumentException>(() => Brent.FindRoot(x => x, -1, 1, 0));
        Assert.Throws<ArgumentException>(() => Brent.FindRoot(_ => double.NaN, -1, 1, Tolerance));
    }

    public static TheoryData<string, double, double, double> Minima => new()
    {
        // name, a, b, exact minimum
        { "parabola", -4.0, 6.0, 1.7 },
        { "cosine", 0.0, 6.0, Math.PI },
        { "flat quartic", -2.0, 2.0, 0.4 },
        { "kink", -5.0, 5.0, -1.3 },
        { "increasing", 2.0, 9.0, 2.0 },
        { "decreasing", 2.0, 9.0, 9.0 },
    };

    [Theory]
    [MemberData(nameof(Minima))]
    public void Finds_known_minima_within_the_stated_bound(string name, double a, double b, double minimum)
    {
        Func<double, double> f = name switch
        {
            "parabola" => x => ((x - 1.7) * (x - 1.7)) + 3.0,
            "cosine" => Math.Cos,
            // Flat to fourth order, so parabolic steps converge slowly.
            "flat quartic" => x => Math.Pow(x - 0.4, 4),
            // Not differentiable at the minimum, so parabolic steps do not apply there.
            "kink" => x => Math.Abs(x + 1.3),
            "increasing" => x => x,
            "decreasing" => x => -x,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        var found = Brent.Minimize(f, a, b, Tolerance);

        double bound = 2 * (Tolerance + (Math.Sqrt(MachineEpsilon) * Math.Abs(found.X)));
        Assert.InRange(found.X, minimum - bound, minimum + bound);
        Assert.Equal(f(found.X), found.Value);
    }

    [Fact]
    public void Random_parabolas_and_asymmetric_wells_are_minimized_within_the_bound()
    {
        var random = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            double center = (random.NextDouble() * 40) - 20;
            double width = Math.Pow(10, (random.NextDouble() * 3) - 1);
            double a = center - (random.NextDouble() * width) - 1e-9;
            double b = center + (random.NextDouble() * width) + 1e-9;
            double left = (random.NextDouble() * 10) + 0.1;
            double right = (random.NextDouble() * 10) + 0.1;
            double tolerance = Math.Pow(10, -2 - (random.NextDouble() * 4));
            // Different curvature either side of the minimum, so a parabola never fits exactly.
            double F(double x) => x < center ? left * (x - center) * (x - center) : right * (x - center) * (x - center);

            var found = Brent.Minimize(F, a, b, tolerance);

            double bound = 2 * (tolerance + (Math.Sqrt(MachineEpsilon) * Math.Abs(found.X)));
            Assert.InRange(found.X, center - bound, center + bound);
            Assert.True(found.Evaluations < 200, $"{found.Evaluations} evaluations");
        }
    }

    [Fact]
    public void Rejects_an_empty_interval_or_bad_tolerance()
    {
        Assert.Throws<ArgumentException>(() => Brent.Minimize(x => x, 1, 1, Tolerance));
        Assert.Throws<ArgumentException>(() => Brent.Minimize(x => x, 2, 1, Tolerance));
        Assert.Throws<ArgumentException>(() => Brent.Minimize(x => x, 0, 1, -1));
        Assert.Throws<ArgumentException>(() => Brent.Minimize(_ => double.NaN, 0, 1, Tolerance));
    }
}
