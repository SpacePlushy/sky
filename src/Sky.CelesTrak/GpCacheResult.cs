namespace Sky.CelesTrak;

/// <summary>Where the element sets in a <see cref="GpCacheResult"/> came from.</summary>
public enum GpDataSource
{
    /// <summary>No data is available: nothing cached and no successful download.</summary>
    None,

    /// <summary>Served from the disk cache without a request.</summary>
    Cache,

    /// <summary>Downloaded from CelesTrak during this call.</summary>
    Downloaded,
}

/// <summary>Element sets for one CelesTrak group, and what happened getting them.</summary>
public sealed record GpCacheResult
{
    /// <summary>The CelesTrak group name.</summary>
    public required string Group { get; init; }

    /// <summary>The element sets, or empty when none are available.</summary>
    public required IReadOnlyList<GpRecord> Records { get; init; }

    /// <summary>Where the records came from.</summary>
    public required GpDataSource Source { get; init; }

    /// <summary>When the records were downloaded from CelesTrak, if any are available.</summary>
    public DateTimeOffset? DownloadedUtc { get; init; }

    /// <summary>Problems a person should know about, such as a CelesTrak error or stale data.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
