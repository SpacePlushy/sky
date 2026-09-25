namespace Sky.Settings;

/// <summary>
/// A clock that reads <paramref name="start"/> at the moment it is created and runs at the rate of
/// <paramref name="inner"/> from there: the <c>Clock:StartUtc</c> setting.
/// </summary>
/// <param name="inner">The real clock.</param>
/// <param name="start">The instant the clock shows when created.</param>
public sealed class StartedClock(TimeProvider inner, DateTimeOffset start) : TimeProvider
{
    private readonly TimeSpan _offset = start - inner.GetUtcNow();

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + _offset;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    /// <inheritdoc />
    public override long TimestampFrequency => inner.TimestampFrequency;

    /// <inheritdoc />
    public override long GetTimestamp() => inner.GetTimestamp();
}
