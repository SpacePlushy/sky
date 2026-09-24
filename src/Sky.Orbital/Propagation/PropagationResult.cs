namespace Sky.Orbital.Propagation;

/// <summary>The outcome of one SGP4 propagation: a state, or the error SGP4 reported.</summary>
public readonly record struct PropagationResult
{
    private readonly TemeState _state;

    internal PropagationResult(TemeState state)
    {
        _state = state;
        Error = Sgp4Error.None;
    }

    internal PropagationResult(Sgp4Error error)
    {
        _state = default;
        Error = error;
    }

    /// <summary>The SGP4 error code, or <see cref="Sgp4Error.None"/>.</summary>
    public Sgp4Error Error { get; }

    /// <summary>True when SGP4 produced a valid state.</summary>
    public bool Succeeded => Error == Sgp4Error.None;

    /// <summary>The propagated state.</summary>
    /// <exception cref="InvalidOperationException">Propagation failed. Check <see cref="Error"/>.</exception>
    public TemeState State => Succeeded
        ? _state
        : throw new InvalidOperationException($"SGP4 propagation failed: {Error}.");
}
