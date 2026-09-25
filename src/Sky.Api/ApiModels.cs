namespace Sky.Api;

// The JSON the dashboard reads. Every instant is UTC and serializes with a Z (DateTime of kind
// Utc); the browser converts to the observer's IANA zone only for display.

/// <summary>The observer and dashboard settings.</summary>
internal sealed record ConfigResponse(
    ObserverDto Observer,
    double MinimumElevationDeg,
    IReadOnlyList<long> Satellites,
    bool Offline,
    DateTime ServerTimeUtc);

/// <summary>Where the observer is and which zone to show times in.</summary>
internal sealed record ObserverDto(string Name, double LatitudeDeg, double LongitudeDeg, double HeightM, string TimeZone);

/// <summary>A satellite the cached groups contain.</summary>
internal sealed record SatelliteSummary(long Id, string Name, string Group, DateTime EpochUtc, double AgeDays, bool Featured);

/// <summary>Where a satellite is at one instant.</summary>
internal sealed record NowResponse(
    DateTime TimeUtc,
    SatelliteInfo Satellite,
    SubpointDto Position,
    LookDto Look,
    bool Sunlit,
    SunDto Sun,
    double FootprintRadiusDeg,
    PassDto? CurrentPass,
    IReadOnlyList<string> Warnings);

/// <summary>Which element set was used.</summary>
internal sealed record SatelliteInfo(long Id, string Name, DateTime EpochUtc, double AgeDays, double PeriodMinutes);

/// <summary>The point below the satellite, its height, and its speed.</summary>
internal sealed record SubpointDto(double LatitudeDeg, double LongitudeDeg, double AltitudeKm, double SpeedKmS);

/// <summary>Where the satellite appears from the observer.</summary>
internal sealed record LookDto(double AzimuthDeg, double ElevationDeg, double RangeKm, double RangeRateKmS);

/// <summary>The Sun seen from the observer, and the point on the Earth it is overhead.</summary>
internal sealed record SunDto(double ElevationDeg, double SubsolarLatitudeDeg, double SubsolarLongitudeDeg);

/// <summary>The ground track around an instant.</summary>
internal sealed record TrackResponse(DateTime FromUtc, DateTime ToUtc, double StepSeconds, IReadOnlyList<TrackPoint> Points, IReadOnlyList<string> Warnings);

/// <summary>One ground-track point.</summary>
internal sealed record TrackPoint(DateTime TimeUtc, double LatitudeDeg, double LongitudeDeg, double AltitudeKm, bool Sunlit);

/// <summary>Passes over the observer in a window.</summary>
internal sealed record PassesResponse(
    DateTime FromUtc,
    DateTime ToUtc,
    double MinimumElevationDeg,
    IReadOnlyList<PassDto> Passes,
    DateTime? AboveMinimumAtStartSinceUtc,
    DateTime? AboveMinimumAtEndUntilUtc,
    string? StoppedBy,
    DateTime? StoppedAtUtc,
    IReadOnlyList<string> Warnings);

/// <summary>One pass, its visible parts, and its path across the sky.</summary>
internal sealed record PassDto(
    EventDto Rise,
    EventDto Culmination,
    EventDto Set,
    double PeakUncertaintyDeg,
    IReadOnlyList<VisibleDto> Visible,
    IReadOnlyList<SkyPoint> Path);

/// <summary>A moment in a pass.</summary>
internal sealed record EventDto(DateTime TimeUtc, double AzimuthDeg, double ElevationDeg);

/// <summary>A visible part of a pass and what starts and ends it.</summary>
internal sealed record VisibleDto(EventDto Start, string StartsBecause, EventDto Highest, EventDto End, string EndsBecause);

/// <summary>A point on the sky-plot path.</summary>
internal sealed record SkyPoint(DateTime TimeUtc, double AzimuthDeg, double ElevationDeg, bool Sunlit);

/// <summary>Service status and data age.</summary>
internal sealed record HealthResponse(string Status, bool Offline, DateTime ServerTimeUtc, IReadOnlyList<GroupHealth> Groups);

/// <summary>What the cache holds for one group.</summary>
internal sealed record GroupHealth(string Group, int Records, string Source, DateTime? DownloadedUtc, IReadOnlyList<string> Warnings);
