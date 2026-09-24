using Sky.Orbital.Elements;

namespace Sky.Orbital.Tests;

/// <summary>Element sets shared across tests.</summary>
internal static class TestElements
{
    /// <summary>ISS (ZARYA), NORAD 25544, from CelesTrak GROUP=stations, downloaded 2026-09-24.</summary>
    public static readonly MeanElements Iss20260924 = new()
    {
        CatalogNumber = 25544,
        Epoch = new DateTimeOffset(2026, 9, 24, 3, 24, 21, TimeSpan.Zero).AddTicks(4_525_440),
        MeanMotion = 15.49258637,
        Eccentricity = 0.00046914,
        Inclination = 51.6318,
        RightAscensionOfAscendingNode = 170.3464,
        ArgumentOfPericenter = 174.6338,
        MeanAnomaly = 185.4701,
        BStar = 0.00018115501,
        MeanMotionDot = 9.634e-5,
        MeanMotionDdot = 0,
    };
}
