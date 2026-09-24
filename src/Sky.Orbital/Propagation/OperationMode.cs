namespace Sky.Orbital.Propagation;

/// <summary>SGP4 operation mode, as defined in Vallado's reference code.</summary>
public enum OperationMode
{
    /// <summary>Improved mode 'i'. Matches Vallado's verification output, python-sgp4, and Skyfield.</summary>
    Improved,

    /// <summary>AFSPC mode 'a'. Reproduces the original Air Force implementation.</summary>
    Afspc,
}
