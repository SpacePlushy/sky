namespace Sky.Orbital.Frames;

/// <summary>Position and velocity in Earth-fixed coordinates.</summary>
/// <param name="Position">Position in kilometers.</param>
/// <param name="Velocity">Velocity relative to the rotating Earth, in kilometers per second.</param>
public readonly record struct EcefState(Vec3 Position, Vec3 Velocity);
