namespace Sky.Orbital;

/// <summary>A three-component vector of doubles. The frame and units depend on context.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    /// <summary>Euclidean length.</summary>
    public double Length => Math.Sqrt(Dot(this));

    /// <summary>Dot product.</summary>
    public double Dot(Vec3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    /// <summary>Component-wise sum.</summary>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Component-wise difference.</summary>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Scales every component.</summary>
    public static Vec3 operator *(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
}
