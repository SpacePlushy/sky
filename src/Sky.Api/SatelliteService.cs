using Sky.CelesTrak;
using Sky.Orbital;
using Sky.Orbital.Astronomy;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;
using Sky.Settings;

namespace Sky.Api;

/// <summary>An error the API reports as RFC 9457 problem details.</summary>
internal sealed class ApiException(int status, string title, string detail) : Exception(detail)
{
    public int Status { get; } = status;

    public string Title { get; } = title;
}

/// <summary>
/// The dashboard's view of the orbital core: finds element sets through the policy cache and runs
/// the same code as the CLI. Thread-safe: each call builds its own propagator, since
/// <see cref="Sgp4Propagator"/> is not.
/// </summary>
internal sealed class SatelliteService(SkySettings settings, GpCache cache, TimeProvider time) : IDisposable
{
    // How long group data is reused in memory before asking the cache again. The cache itself
    // decides when CelesTrak may be asked; this only saves reparsing on every poll.
    private static readonly TimeSpan MemoryLifetime = TimeSpan.FromMinutes(1);

    // Mean Earth radius (IUGG), for the display-only footprint circle.
    private const double MeanEarthRadiusKm = 6371.0088;

    private const double RadiansToDegrees = 180.0 / Math.PI;
    private static readonly TimeSpan PathStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrackStep = TimeSpan.FromSeconds(30);
    private const int MaximumTrackPoints = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TopocentricFrame _observer = new(settings.Observer);
    private (DateTimeOffset LoadedAt, IReadOnlyList<GpCacheResult> Groups)? _memory;

    public DateTimeOffset Now => time.GetUtcNow();

    public SkySettings Settings => settings;

    public void Dispose() => _gate.Dispose();

    /// <summary>Every configured group, through the cache, reused in memory for a minute.</summary>
    public async Task<IReadOnlyList<GpCacheResult>> GroupsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = time.GetUtcNow();
            if (_memory is { } memory && now >= memory.LoadedAt && now - memory.LoadedAt < MemoryLifetime)
            {
                return memory.Groups;
            }

            var groups = new List<GpCacheResult>();
            foreach (string group in settings.Groups)
            {
                groups.Add(await cache.GetGroupAsync(group, cancellationToken).ConfigureAwait(false));
            }

            _memory = (now, groups);
            return groups;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SatelliteSummary>> SatellitesAsync(CancellationToken cancellationToken)
    {
        var groups = await GroupsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = time.GetUtcNow();
        return groups
            .SelectMany(g => g.Records.Select(r => (Group: g.Group, Record: r)))
            .GroupBy(x => x.Record.Elements.CatalogNumber)
            .Select(g => g.MaxBy(x => x.Record.Elements.Epoch))
            .Select(x => new SatelliteSummary(
                x.Record.Elements.CatalogNumber,
                x.Record.Name ?? $"NORAD {x.Record.Elements.CatalogNumber}",
                x.Group,
                Utc(x.Record.Elements.Epoch),
                (now - x.Record.Elements.Epoch).TotalDays,
                settings.Satellites.Contains(x.Record.Elements.CatalogNumber)))
            .OrderBy(s => s.Featured ? settings.Satellites.ToList().IndexOf(s.Id) : int.MaxValue)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<NowResponse> NowAsync(long id, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        var (record, warnings) = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        Sgp4Propagator propagator = CreatePropagator(record);
        DateTimeOffset t = at ?? time.GetUtcNow();
        EcefState ecef = Propagate(propagator, t);

        Geodetic subpoint = Wgs84.FromEcef(ecef.Position);
        LookAngles look = _observer.LookAt(ecef);
        Vec3 sun = Sun.PositionEcef(t);
        double sunElevation = _observer.LookAt(new EcefState(sun, default)).ElevationDegrees;
        double footprint = Math.Acos(MeanEarthRadiusKm / (MeanEarthRadiusKm + Math.Max(subpoint.HeightKm, 0))) * RadiansToDegrees;
        double inertialSpeed = propagator.Propagate(t).State.Velocity.Length;

        // A zero-length window finds the pass the satellite is in now, if any: the finder follows a
        // pass in progress back to its rise and forward to its set.
        PassSearchResult search = PassFinder.Find(propagator, _observer, t, t, settings.MinimumElevationDegrees);
        SatellitePass? current = search.Passes.FirstOrDefault(p => p.Rise.Time <= t && t <= p.Set.Time);

        return new NowResponse(
            Utc(t),
            Info(record, t),
            new SubpointDto(subpoint.LatitudeDegrees, subpoint.LongitudeDegrees, subpoint.HeightKm, inertialSpeed),
            new LookDto(look.AzimuthDegrees, look.ElevationDegrees, look.RangeKm, look.RangeRateKmPerSecond),
            EarthShadow.IsSunlit(ecef.Position, sun),
            new SunDto(sunElevation, Math.Atan2(sun.Z, Math.Sqrt((sun.X * sun.X) + (sun.Y * sun.Y))) * RadiansToDegrees, Math.Atan2(sun.Y, sun.X) * RadiansToDegrees),
            footprint,
            current is null ? null : Pass(current, propagator),
            warnings);
    }

    public async Task<TrackResponse> TrackAsync(long id, double? minutes, CancellationToken cancellationToken)
    {
        var (record, warnings) = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        Sgp4Propagator propagator = CreatePropagator(record);
        DateTimeOffset t = time.GetUtcNow();

        // One orbit either side by default, so the map shows where the satellite has been and where
        // it is going; long periods are thinned to keep the response small.
        double span = minutes ?? PeriodMinutes(record);
        if (!(span > 0) || span > 2880)
        {
            throw new ApiException(400, "Invalid track length", "minutes must be more than 0 and at most 2880.");
        }

        TimeSpan half = TimeSpan.FromMinutes(span);
        TimeSpan step = TrackStep;
        if ((half * 2) / step > MaximumTrackPoints)
        {
            step = (half * 2) / MaximumTrackPoints;
        }

        var points = new List<TrackPoint>();
        for (DateTimeOffset s = t - half; s <= t + half; s += step)
        {
            PropagationResult result = propagator.Propagate(s);
            if (!result.Succeeded)
            {
                warnings = [.. warnings, $"SGP4 cannot propagate NORAD {id} to {s:yyyy-MM-dd HH:mm:ss} UTC ({result.Error}); the track stops there."];
                if (s > t)
                {
                    break;
                }

                points.Clear();
                continue;
            }

            EcefState ecef = EarthRotation.TemeToEcef(result.State, s);
            Geodetic g = Wgs84.FromEcef(ecef.Position);
            points.Add(new TrackPoint(Utc(s), g.LatitudeDegrees, g.LongitudeDegrees, g.HeightKm, EarthShadow.IsSunlit(ecef.Position, Sun.PositionEcef(s))));
        }

        return new TrackResponse(Utc(t - half), Utc(t + half), step.TotalSeconds, points, warnings);
    }

    public async Task<PassesResponse> PassesAsync(long id, int? days, double? minimumElevation, CancellationToken cancellationToken)
    {
        int d = days ?? 7;
        if (d is < 1 or > 10)
        {
            throw new ApiException(400, "Invalid number of days", "days must be from 1 to 10.");
        }

        double minimum = minimumElevation ?? settings.MinimumElevationDegrees;
        if (!(minimum >= 0 && minimum < 90))
        {
            throw new ApiException(400, "Invalid minimum elevation", "minElevation must be from 0 to below 90 degrees.");
        }

        var (record, warnings) = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        Sgp4Propagator propagator = CreatePropagator(record);
        DateTimeOffset t = time.GetUtcNow();
        PassSearchResult search = PassFinder.Find(propagator, _observer, t, t.AddDays(d), minimum);

        return new PassesResponse(
            Utc(t),
            Utc(t.AddDays(d)),
            minimum,
            search.Passes.Select(p => Pass(p, propagator)).ToList(),
            search.AboveMinimumAtStartSince is { } since ? Utc(since) : null,
            search.AboveMinimumAtEndUntil is { } until ? Utc(until) : null,
            search.StoppedAt is null ? null : search.StoppedBy.ToString(),
            search.StoppedAt is { } stopped ? Utc(stopped) : null,
            warnings);
    }

    public async Task<HealthResponse> HealthAsync(CancellationToken cancellationToken)
    {
        var groups = await GroupsAsync(cancellationToken).ConfigureAwait(false);
        bool anyData = groups.Any(g => g.Records.Count > 0);
        return new HealthResponse(
            anyData ? "ok" : "no data",
            settings.Offline,
            Utc(time.GetUtcNow()),
            groups.Select(g => new GroupHealth(g.Group, g.Records.Count, g.Source.ToString(), g.DownloadedUtc is { } dl ? Utc(dl) : null, g.Warnings)).ToList());
    }

    private PassDto Pass(SatellitePass pass, Sgp4Propagator propagator)
    {
        var path = new List<SkyPoint>();
        for (DateTimeOffset s = pass.Rise.Time; s < pass.Set.Time; s += PathStep)
        {
            path.Add(SkyPointAt(s, propagator));
        }

        path.Add(SkyPointAt(pass.Set.Time, propagator));
        var visible = Visibility.Windows(pass, propagator, _observer)
            .Select(w => new VisibleDto(Event(w.Start), Change(w.StartsBecause), Event(w.Highest), Event(w.End), Change(w.EndsBecause)))
            .ToList();
        return new PassDto(Event(pass.Rise), Event(pass.Culmination), Event(pass.Set), pass.PeakElevationUncertaintyDegrees, visible, path);
    }

    private SkyPoint SkyPointAt(DateTimeOffset t, Sgp4Propagator propagator)
    {
        EcefState ecef = Propagate(propagator, t);
        LookAngles look = _observer.LookAt(ecef);
        return new SkyPoint(Utc(t), look.AzimuthDegrees, look.ElevationDegrees, EarthShadow.IsSunlit(ecef.Position, Sun.PositionEcef(t)));
    }

    private async Task<(GpRecord Record, IReadOnlyList<string> Warnings)> FindAsync(long id, CancellationToken cancellationToken)
    {
        var groups = await GroupsAsync(cancellationToken).ConfigureAwait(false);
        // MaxBy on value tuples throws when nothing matches, so test for a match first.
        var matches = groups
            .SelectMany(g => g.Records.Select(r => (Group: g, Record: r)))
            .Where(x => x.Record.Elements.CatalogNumber == id)
            .ToList();
        if (matches.Count == 0)
        {
            string[] unavailable = groups.Where(g => g.Records.Count == 0).SelectMany(g => g.Warnings).ToArray();
            if (groups.All(g => g.Records.Count == 0))
            {
                throw new ApiException(503, "No orbital data", "No element sets are available. " + string.Join(" ", unavailable));
            }

            throw new ApiException(404, "Unknown satellite", $"NORAD {id} is not in {string.Join(", ", groups.Where(g => g.Records.Count > 0).Select(g => g.Group))}.");
        }

        var match = matches.MaxBy(x => x.Record.Elements.Epoch);
        var warnings = new List<string>(match.Group.Warnings);
        double age = (time.GetUtcNow() - match.Record.Elements.Epoch).TotalDays;
        if (age > 3)
        {
            warnings.Add($"{match.Record.Name} elements are {age:F1} days old; predictions degrade by kilometers per day of element age.");
        }

        return (match.Record, warnings);
    }

    private static Sgp4Propagator CreatePropagator(GpRecord record)
    {
        try
        {
            return Sgp4Propagator.Create(record.ToSgp4Elements());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new ApiException(422, "Cannot propagate", ex.Message);
        }
    }

    private static EcefState Propagate(Sgp4Propagator propagator, DateTimeOffset t)
    {
        PropagationResult result = propagator.Propagate(t);
        return result.Succeeded
            ? EarthRotation.TemeToEcef(result.State, t)
            : throw new ApiException(422, "Cannot propagate", $"SGP4 cannot propagate NORAD {propagator.Elements.CatalogNumber} to {t:yyyy-MM-dd HH:mm:ss} UTC: {result.Error}.");
    }

    private static SatelliteInfo Info(GpRecord record, DateTimeOffset t) => new(
        record.Elements.CatalogNumber,
        record.Name ?? $"NORAD {record.Elements.CatalogNumber}",
        Utc(record.Elements.Epoch),
        (t - record.Elements.Epoch).TotalDays,
        PeriodMinutes(record));

    private static double PeriodMinutes(GpRecord record) => 1440.0 / record.Elements.MeanMotion;

    private static EventDto Event(PassEvent e) => new(Utc(e.Time), e.AzimuthDegrees, e.ElevationDegrees);

    private static string Change(VisibilityChange change) => change switch
    {
        VisibilityChange.Rise => "rise",
        VisibilityChange.Set => "set",
        VisibilityChange.LeavesShadow => "leavesShadow",
        VisibilityChange.EntersShadow => "entersShadow",
        VisibilityChange.SkyDarkens => "skyDarkens",
        VisibilityChange.SkyBrightens => "skyBrightens",
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    /// <summary>An instant as a UTC DateTime, which JSON writes with a Z.</summary>
    internal static DateTime Utc(DateTimeOffset instant) => instant.UtcDateTime;
}
