using Sky.Orbital.Astronomy;
using Sky.Orbital.Frames;

namespace Sky.Orbital.Tests.Astronomy;

/// <summary>
/// The shadow test against exact geometry: points directly behind or beside the Earth, lines built
/// to graze the ellipsoid, a brute-force march along the line to the Sun, and continuity.
/// </summary>
public class EarthShadowTests
{
    private const double A = Wgs84.EquatorialRadius;
    private const double B = A * (1.0 - Wgs84.Flattening);
    private static readonly Vec3 SunOnX = new(149_597_870.7, 0, 0);

    [Fact]
    public void Directly_behind_the_earth_is_shadow_and_beside_it_is_sunlit()
    {
        Assert.False(EarthShadow.IsSunlit(new Vec3(-(A + 400), 0, 0), SunOnX));
        Assert.Equal(-A, EarthShadow.Function(new Vec3(-(A + 400), 0, 0), SunOnX), 1e-9);

        // Beside the Earth, with the Sun 1 AU away rather than infinitely far, the line to it
        // leans toward the center by r² / 1 AU = 0.3 km along its length, so the function is the
        // line's distance from the center, |s × sun| / |sun − s|, minus a: r³/(2·AU²) ≈ 7 mm under 400 km.
        double r = A + 400;
        double lineDistance = r * SunOnX.X / Math.Sqrt((SunOnX.X * SunOnX.X) + (r * r));
        Assert.True(EarthShadow.IsSunlit(new Vec3(0, r, 0), SunOnX));
        Assert.Equal(lineDistance - A, EarthShadow.Function(new Vec3(0, r, 0), SunOnX), 1e-9);

        // On the day side, always sunlit.
        Assert.True(EarthShadow.IsSunlit(new Vec3(A + 400, 0, 0), SunOnX));
    }

    [Fact]
    public void In_the_equatorial_plane_the_function_is_the_lines_distance_from_the_center_minus_a()
    {
        // With everything in z = 0 the stretch does nothing, so the function must equal the
        // distance from the center to the line through the satellite and the Sun, computed here by
        // a different formula: |s × sun| / |sun − s|.
        var random = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            double angle = random.NextDouble() * 2 * Math.PI;
            double radius = A + 200 + (random.NextDouble() * 40_000);
            var s = new Vec3(radius * Math.Cos(angle), radius * Math.Sin(angle), 0);
            if (s.X > 0)
            {
                continue; // on the Sun's side the line heads away from the center; covered elsewhere
            }

            double cross = Math.Abs((s.X * SunOnX.Y) - (s.Y * SunOnX.X));
            double distance = cross / (SunOnX - s).Length;

            Assert.Equal(distance - A, EarthShadow.Function(s, SunOnX), 1e-7);
        }
    }

    [Fact]
    public void The_shadow_edge_over_a_pole_is_at_the_polar_radius_not_the_equatorial()
    {
        // A satellite behind the Earth, level with the pole, with the Sun so far away that its rays
        // are parallel to x to about 6×10⁻¹² rad (b / 10¹⁵ km), 2×10⁻⁸ km over 3,000 km: the line grazes the ellipsoid at height z = b.
        // A spherical Earth of radius a would put the edge 21.4 km higher.
        var farSun = new Vec3(1e15, 0, 0);
        Assert.False(EarthShadow.IsSunlit(new Vec3(-3000, 0, B - 0.001), farSun));
        Assert.True(EarthShadow.IsSunlit(new Vec3(-3000, 0, B + 0.001), farSun));
        Assert.True(EarthShadow.IsSunlit(new Vec3(-3000, 0, (A + B) / 2), farSun));
    }

    [Fact]
    public void Lines_built_to_graze_the_ellipsoid_give_zero()
    {
        // Take a point P on the ellipsoid and a direction t in its tangent plane. A satellite on the
        // line P − L·t with the Sun at P + 1 AU·t sees the Sun's center exactly on the limb, so the
        // function must be zero. Its scale is kilometers, so rounding allows about 1e-8.
        var random = new Random(12);
        for (int i = 0; i < 2000; i++)
        {
            double lat = Math.Asin((random.NextDouble() * 2) - 1) * 180 / Math.PI;
            double lon = (random.NextDouble() * 360) - 180;
            Vec3 p = Wgs84.ToEcef(new Geodetic(lat, lon, 0));

            // Normal to the ellipsoid at P, and a random unit vector perpendicular to it.
            var normal = new Vec3(p.X / (A * A), p.Y / (A * A), p.Z / (B * B));
            normal *= 1.0 / normal.Length;
            var arbitrary = new Vec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            Vec3 tangent = arbitrary - (normal * arbitrary.Dot(normal));
            tangent *= 1.0 / tangent.Length;

            double along = 100 + (random.NextDouble() * 5000);
            Vec3 satellite = p - (tangent * along);
            Vec3 sun = p + (tangent * 149_597_870.7);

            Assert.Equal(0.0, EarthShadow.Function(satellite, sun), 1e-7);
        }
    }

    [Fact]
    public void Agrees_with_a_brute_force_march_toward_the_sun()
    {
        // Independent of the stretching argument: walk from the satellite toward the Sun in 0.5 km
        // steps for 20,000 km (beyond which the line cannot re-enter the Earth from any orbit here)
        // and see whether any point is inside the ellipsoid. Cases within 2 km of the edge are
        // skipped, since the march's step could miss a short chord there.
        var random = new Random(13);
        int shadowed = 0;
        int lit = 0;
        for (int i = 0; i < 600; i++)
        {
            double r = A + 150 + (random.NextDouble() * 3000);
            var satellite = RandomDirection(random) * r;
            var sun = RandomDirection(random) * 149_597_870.7;
            double g = EarthShadow.Function(satellite, sun);
            if (Math.Abs(g) < 2.0)
            {
                continue;
            }

            Vec3 step = (sun - satellite) * (0.5 / (sun - satellite).Length);
            bool blocked = false;
            for (int k = 1; k <= 40_000 && !blocked; k++)
            {
                Vec3 q = satellite + (step * k);
                blocked = ((q.X * q.X) / (A * A)) + ((q.Y * q.Y) / (A * A)) + ((q.Z * q.Z) / (B * B)) < 1.0;
            }

            Assert.Equal(!blocked, EarthShadow.IsSunlit(satellite, sun));
            if (blocked)
            {
                shadowed++;
            }
            else
            {
                lit++;
            }
        }

        // Both outcomes must actually have been exercised.
        Assert.True(shadowed > 50 && lit > 50, $"{shadowed} shadowed, {lit} lit");
    }

    [Fact]
    public void Is_continuous_including_where_its_two_branches_meet()
    {
        // In the stretched space the function is a distance from the center to a line (or to the
        // satellite), so moving the satellite by d changes it by at most |stretch(d)|, plus the
        // line's turn toward the Sun, which is |d| / 1 AU of the ~10,000 km lever: negligible.
        // Half the pairs are built exactly across the branch switch, where the stretched line to the
        // Sun is perpendicular to the stretched position; the rest are random.
        const double Au = 149_597_870.7;
        const double K = 1.0 / (1.0 - Wgs84.Flattening);
        var random = new Random(14);
        int straddling = 0;
        for (int i = 0; i < 5000; i++)
        {
            Vec3 sunDirection = RandomDirection(random);
            Vec3 sun = sunDirection * Au;
            double r = A + 150 + (random.NextDouble() * 3000);
            Vec3 s1;
            if (i % 2 == 0)
            {
                // In the stretched space S = stretch(sun), U = S/|S|, P ⊥ U: along = 0 at s' = r·P + ε0·U,
                // ε0 = (|S| − √(|S|² − 4r²)) / 2. Offset by up to 5 m either side, then unstretch.
                var stretchedSun = new Vec3(sun.X, sun.Y, sun.Z * K);
                double length = stretchedSun.Length;
                Vec3 u = stretchedSun * (1.0 / length);
                Vec3 p = RandomDirection(random);
                p -= u * p.Dot(u);
                p *= 1.0 / p.Length;
                double epsilon0 = (length - Math.Sqrt((length * length) - (4 * r * r))) / 2;
                Vec3 stretched = (p * r) + (u * (epsilon0 + ((random.NextDouble() - 0.5) * 0.01)));
                s1 = new Vec3(stretched.X, stretched.Y, stretched.Z / K);
            }
            else
            {
                s1 = RandomDirection(random) * r;
            }

            Vec3 d = RandomDirection(random) * (random.NextDouble() * 0.01);
            Vec3 s2 = s1 + d;
            straddling += Along(s1, sun) * Along(s2, sun) < 0 ? 1 : 0;

            double change = Math.Abs(EarthShadow.Function(s1, sun) - EarthShadow.Function(s2, sun));
            double stretchedMove = new Vec3(d.X, d.Y, d.Z * K).Length;
            Assert.True(change <= (stretchedMove * (1 + 1e-6)) + 1e-9, $"changed {change} km for a {stretchedMove} km move");
        }

        // Many pairs really do sit on opposite branches.
        Assert.True(straddling > 500, $"Only {straddling} pairs straddle the branch switch.");

        static double Along(Vec3 satellite, Vec3 sun)
        {
            var s = new Vec3(satellite.X, satellite.Y, satellite.Z * K);
            var toSun = new Vec3(sun.X - satellite.X, sun.Y - satellite.Y, (sun.Z - satellite.Z) * K);
            return -s.Dot(toSun * (1.0 / toSun.Length));
        }
    }

    private static Vec3 RandomDirection(Random random)
    {
        double z = (random.NextDouble() * 2) - 1;
        double phi = random.NextDouble() * 2 * Math.PI;
        double rho = Math.Sqrt(1 - (z * z));
        return new Vec3(rho * Math.Cos(phi), rho * Math.Sin(phi), z);
    }
}
