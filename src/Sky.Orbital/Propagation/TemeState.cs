namespace Sky.Orbital.Propagation;

/// <summary>
/// Position and velocity in the True Equator, Mean Equinox (TEME) frame that SGP4 outputs.
/// </summary>
/// <param name="Position">Position in kilometers.</param>
/// <param name="Velocity">Velocity in kilometers per second.</param>
public readonly record struct TemeState(Vec3 Position, Vec3 Velocity);
