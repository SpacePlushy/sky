using Sky.Orbital.Elements;

namespace Sky.CelesTrak;

/// <summary>One general perturbations (GP) element set as CelesTrak publishes it.</summary>
public sealed record GpRecord
{
    /// <summary>Object name, such as "ISS (ZARYA)". Can be missing for analyst objects.</summary>
    public string? Name { get; init; }

    /// <summary>International designator, such as "1998-067A".</summary>
    public string? ObjectId { get; init; }

    /// <summary>Ephemeris type. 0 is SGP4; 4 is SGP4-XP, which SGP4 cannot propagate.</summary>
    public required int EphemerisType { get; init; }

    /// <summary>The mean elements.</summary>
    public required MeanElements Elements { get; init; }

    /// <summary>The elements, for use with SGP4.</summary>
    /// <exception cref="InvalidOperationException">
    /// The element set is not SGP4 (ephemeris type other than 0). SGP4-XP elements (type 4) come
    /// from a different theory, and SGP4 would propagate them to wrong positions without error.
    /// </exception>
    public MeanElements ToSgp4Elements() => EphemerisType == 0
        ? Elements
        : throw new InvalidOperationException(
            $"Element set for NORAD {Elements.CatalogNumber} has ephemeris type {EphemerisType}, not 0 (SGP4). " +
            "Type 4 is SGP4-XP, which SGP4 cannot propagate correctly.");
}
