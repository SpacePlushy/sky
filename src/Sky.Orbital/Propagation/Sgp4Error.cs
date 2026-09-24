namespace Sky.Orbital.Propagation;

/// <summary>SGP4 error codes from Vallado's reference code. Code 5 is unused upstream.</summary>
public enum Sgp4Error
{
    /// <summary>Propagation succeeded.</summary>
    None = 0,

    /// <summary>Mean eccentricity is outside [0, 1).</summary>
    MeanEccentricityOutOfRange = 1,

    /// <summary>Mean motion is not positive.</summary>
    MeanMotionNotPositive = 2,

    /// <summary>Perturbed eccentricity is outside [0, 1].</summary>
    PerturbedEccentricityOutOfRange = 3,

    /// <summary>Semi-latus rectum is negative.</summary>
    SemiLatusRectumNegative = 4,

    /// <summary>The orbit has decayed: the satellite is below the Earth's surface.</summary>
    Decayed = 6,
}
