namespace Sky.Orbital.Elements;

/// <summary>
/// SGP4 mean orbital elements in the units that OMM and TLE data use.
/// The propagator converts them to SGP4's internal units.
/// </summary>
/// <remarks>
/// These are mean elements fitted for SGP4 with WGS-72 constants. They are not
/// osculating elements and must not be used with any other propagator.
/// </remarks>
public sealed record MeanElements
{
    /// <summary>NORAD catalog number. OMM allows up to nine digits.</summary>
    public required long CatalogNumber { get; init; }

    /// <summary>Epoch of the element set, in UTC.</summary>
    public required DateTimeOffset Epoch { get; init; }

    /// <summary>Mean motion, in revolutions per day.</summary>
    public required double MeanMotion { get; init; }

    /// <summary>Eccentricity, dimensionless.</summary>
    public required double Eccentricity { get; init; }

    /// <summary>Inclination, in degrees.</summary>
    public required double Inclination { get; init; }

    /// <summary>Right ascension of the ascending node, in degrees.</summary>
    public required double RightAscensionOfAscendingNode { get; init; }

    /// <summary>Argument of pericenter, in degrees.</summary>
    public required double ArgumentOfPericenter { get; init; }

    /// <summary>Mean anomaly, in degrees.</summary>
    public required double MeanAnomaly { get; init; }

    /// <summary>SGP4 drag term B*, in inverse Earth radii.</summary>
    public required double BStar { get; init; }

    /// <summary>
    /// First derivative of mean motion divided by two, in revolutions per day squared.
    /// SGP4 does not use it.
    /// </summary>
    public double MeanMotionDot { get; init; }

    /// <summary>
    /// Second derivative of mean motion divided by six, in revolutions per day cubed.
    /// SGP4 does not use it.
    /// </summary>
    public double MeanMotionDdot { get; init; }
}
