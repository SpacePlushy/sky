namespace Sky.Orbital.Numerics;

/// <summary>The result of a one-dimensional search: where it ended, the function there, and how long it took.</summary>
/// <param name="X">The root or minimum found.</param>
/// <param name="Value">The function's value at <paramref name="X"/>.</param>
/// <param name="Evaluations">How many times the function was evaluated.</param>
public readonly record struct SearchResult(double X, double Value, int Evaluations);

/// <summary>
/// Brent's methods for a root and for a minimum of a function of one variable, as published in
/// R. P. Brent, <i>Algorithms for Minimization without Derivatives</i> (Prentice-Hall, 1973),
/// chapter 4 (<c>zero</c>) and chapter 5 (<c>localmin</c>).
/// </summary>
/// <remarks>
/// Both are translated from Brent's ALGOL 60 procedures step for step, keeping his variable names
/// so the code can be checked against the book. Each combines a fast step (inverse quadratic or
/// parabolic interpolation) with a safe one (bisection or golden section), and falls back to the
/// safe step whenever the fast one does not shrink the interval quickly enough. That gives
/// guaranteed convergence: the zero finder needs at most about the square of the evaluations
/// bisection would, and usually far fewer.
/// </remarks>
public static class Brent
{
    // The unit roundoff of a double: 2^-53. Brent's "macheps".
    private const double MachineEpsilon = 1.1102230246251565e-16;

    // Brent's recommended relative tolerance for minimization, the square root of macheps. Near a
    // minimum a function changes only with the square of the distance, so no method can place the
    // minimum more finely than this relative to its position.
    private static readonly double SqrtMachineEpsilon = Math.Sqrt(MachineEpsilon);

    /// <summary>
    /// Finds a zero of <paramref name="f"/> in [<paramref name="a"/>, <paramref name="b"/>], where the
    /// function's values at the two ends have opposite signs or one is zero.
    /// </summary>
    /// <param name="f">A function continuous on the interval.</param>
    /// <param name="a">One end of the bracket.</param>
    /// <param name="b">The other end.</param>
    /// <param name="tolerance">
    /// The returned <see cref="SearchResult.X"/> lies within this distance of a point where
    /// <paramref name="f"/> changes sign (or is zero), apart from rounding of order 4·macheps·|x|.
    /// </param>
    /// <exception cref="ArgumentException">The ends do not bracket a zero, or an argument is not finite.</exception>
    public static SearchResult FindRoot(Func<double, double> f, double a, double b, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (!double.IsFinite(a) || !double.IsFinite(b) || !(tolerance > 0) || !double.IsFinite(tolerance))
        {
            throw new ArgumentException("The bracket must be finite and the tolerance positive.");
        }

        double fa = f(a);
        double fb = f(b);
        int evaluations = 2;
        if (double.IsNaN(fa) || double.IsNaN(fb))
        {
            throw new ArgumentException("The function is not a number at an end of the bracket.");
        }

        if (fa == 0)
        {
            return new SearchResult(a, fa, evaluations);
        }

        if (fb == 0)
        {
            return new SearchResult(b, fb, evaluations);
        }

        if ((fa > 0) == (fb > 0))
        {
            throw new ArgumentException($"f({a}) = {fa} and f({b}) = {fb} have the same sign, so they do not bracket a zero.");
        }

        // Brent stops when the bracket [b, c] is at most 2·tol wide and returns b, an end of it. Half
        // the requested tolerance keeps b within the tolerance of the sign change.
        double t = tolerance / 2.0;
        double c = a;
        double fc = fa;
        double d = b - a;
        double e = d;
        while (true)
        {
            if ((fb > 0) == (fc > 0))
            {
                c = a;
                fc = fa;
                d = b - a;
                e = d;
            }

            if (Math.Abs(fc) < Math.Abs(fb))
            {
                a = b;
                b = c;
                c = a;
                fa = fb;
                fb = fc;
                fc = fa;
            }

            double tol = (2.0 * MachineEpsilon * Math.Abs(b)) + t;
            double m = 0.5 * (c - b);
            if (Math.Abs(m) <= tol || fb == 0)
            {
                return new SearchResult(b, fb, evaluations);
            }

            if (Math.Abs(e) < tol || Math.Abs(fa) <= Math.Abs(fb))
            {
                // Bisection.
                d = m;
                e = m;
            }
            else
            {
                double s = fb / fa;
                double p;
                double q;
                if (a == c)
                {
                    // Linear interpolation.
                    p = 2.0 * m * s;
                    q = 1.0 - s;
                }
                else
                {
                    // Inverse quadratic interpolation.
                    q = fa / fc;
                    double r = fb / fc;
                    p = s * ((2.0 * m * q * (q - r)) - ((b - a) * (r - 1.0)));
                    q = (q - 1.0) * (r - 1.0) * (s - 1.0);
                }

                if (p > 0)
                {
                    q = -q;
                }
                else
                {
                    p = -p;
                }

                s = e;
                e = d;
                if ((2.0 * p) < ((3.0 * m * q) - Math.Abs(tol * q)) && p < Math.Abs(0.5 * s * q))
                {
                    d = p / q;
                }
                else
                {
                    d = m;
                    e = m;
                }
            }

            a = b;
            fa = fb;
            b += Math.Abs(d) > tol ? d : (m > 0 ? tol : -tol);
            fb = f(b);
            evaluations++;
            if (double.IsNaN(fb))
            {
                throw new ArgumentException($"The function is not a number at {b}.");
            }
        }
    }

    /// <summary>
    /// Finds a minimum of <paramref name="f"/> in [<paramref name="a"/>, <paramref name="b"/>]. If the
    /// function has one minimum there and no other local minimum, this is it; otherwise it is some
    /// local minimum, or an end of the interval.
    /// </summary>
    /// <param name="f">The function to minimize.</param>
    /// <param name="a">The lower end of the interval.</param>
    /// <param name="b">The upper end, greater than <paramref name="a"/>.</param>
    /// <param name="tolerance">
    /// The absolute part of Brent's tolerance. The returned <see cref="SearchResult.X"/> lies within
    /// 2·(tolerance + √macheps·|x|) of the minimum. Keep |x| small, for example by measuring from
    /// the middle of the interval, so the relative part stays negligible.
    /// </param>
    /// <exception cref="ArgumentException">The interval is empty or an argument is not finite.</exception>
    public static SearchResult Minimize(Func<double, double> f, double a, double b, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (!double.IsFinite(a) || !double.IsFinite(b) || !(a < b) || !(tolerance > 0) || !double.IsFinite(tolerance))
        {
            throw new ArgumentException("The interval must be finite with a < b, and the tolerance positive.");
        }

        // The golden-section ratio, (3 - √5) / 2.
        double c = 0.5 * (3.0 - Math.Sqrt(5.0));
        double t = tolerance;
        double eps = SqrtMachineEpsilon;

        double x = a + (c * (b - a));
        double v = x;
        double w = x;
        double d = 0.0;
        double e = 0.0;
        double fx = f(x);
        int evaluations = 1;
        if (double.IsNaN(fx))
        {
            throw new ArgumentException($"The function is not a number at {x}.");
        }

        double fv = fx;
        double fw = fx;
        while (true)
        {
            double m = 0.5 * (a + b);
            double tol = (eps * Math.Abs(x)) + t;
            double t2 = 2.0 * tol;
            if (Math.Abs(x - m) <= t2 - (0.5 * (b - a)))
            {
                return new SearchResult(x, fx, evaluations);
            }

            double p = 0.0;
            double q = 0.0;
            double r = 0.0;
            if (Math.Abs(e) > tol)
            {
                // Fit a parabola through x, v, and w.
                r = (x - w) * (fx - fv);
                q = (x - v) * (fx - fw);
                p = ((x - v) * q) - ((x - w) * r);
                q = 2.0 * (q - r);
                if (q > 0)
                {
                    p = -p;
                }
                else
                {
                    q = -q;
                }

                r = e;
                e = d;
            }

            double u;
            if (Math.Abs(p) < Math.Abs(0.5 * q * r) && p > q * (a - x) && p < q * (b - x))
            {
                // Parabolic interpolation step. f must not be evaluated too close to a or b.
                d = p / q;
                u = x + d;
                if ((u - a) < t2 || (b - u) < t2)
                {
                    d = x < m ? tol : -tol;
                }
            }
            else
            {
                // Golden-section step.
                e = (x < m ? b : a) - x;
                d = c * e;
            }

            // f must not be evaluated too close to x.
            u = x + (Math.Abs(d) >= tol ? d : (d > 0 ? tol : -tol));
            double fu = f(u);
            evaluations++;
            if (double.IsNaN(fu))
            {
                throw new ArgumentException($"The function is not a number at {u}.");
            }

            if (fu <= fx)
            {
                if (u < x)
                {
                    b = x;
                }
                else
                {
                    a = x;
                }

                v = w;
                fv = fw;
                w = x;
                fw = fx;
                x = u;
                fx = fu;
            }
            else
            {
                if (u < x)
                {
                    a = u;
                }
                else
                {
                    b = u;
                }

                if (fu <= fw || w == x)
                {
                    v = w;
                    fv = fw;
                    w = u;
                    fw = fu;
                }
                else if (fu <= fv || v == x || v == w)
                {
                    v = u;
                    fv = fu;
                }
            }
        }
    }
}
