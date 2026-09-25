using System.Globalization;
using Sky.CelesTrak;
using Sky.Orbital;
using Sky.Orbital.Astronomy;
using Sky.Orbital.Elements;
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

/// <summary>The dashboard's clock, which a demo can start at a fixed instant; kept apart from the framework's TimeProvider.</summary>
/// <param name="Time">The clock.</param>
internal sealed record SkyClock(TimeProvider Time);

/// <summary>
/// The dashboard's view of the orbital core: finds element sets through the policy cache and runs
/// the same code as the CLI. Thread-safe: each call builds its own propagator, since
/// <see cref="Sgp4Propagator"/> is not.
/// </summary>
internal sealed class SatelliteService(SkySettings settings, GpCache cache, SkyClock clock, IHostApplicationLifetime lifetime)
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

    // How far from the element epoch ?at= may ask: well inside SGP4's useful range and the
    // DateTimeOffset range the pass finder's one-day extensions need.
    private static readonly TimeSpan MaximumAtDistance = TimeSpan.FromDays(30);

    private readonly TimeProvider _time = clock.Time;
    private readonly Lock _sync = new();
    private readonly TopocentricFrame _observer = new(settings.Observer);
    private (DateTimeOffset LoadedAt, IReadOnlyList<GpCacheResult> Groups)? _memory;
    private Task<IReadOnlyList<GpCacheResult>>? _refresh;

    public DateTimeOffset Now => _time.GetUtcNow();

    public SkySettings Settings => settings;

    /// <summary>
    /// Every configured group, through the cache, reused in memory for a minute. A refresh is shared
    /// by every caller and runs to completion on the application's lifetime, not a caller's: a
    /// browser that aborts its request ends only its own wait, never a CelesTrak download that the
    /// cache has already counted against the 2-hour rule.
    /// </summary>
    public async Task<IReadOnlyList<GpCacheResult>> GroupsAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<GpCacheResult>> refresh;
        lock (_sync)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_memory is { } memory && now >= memory.LoadedAt && now - memory.LoadedAt < MemoryLifetime)
            {
                return memory.Groups;
            }

            refresh = _refresh ??= LoadGroupsAsync();
        }

        return await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GpCacheResult>> LoadGroupsAsync()
    {
        // Return to the caller first, so _refresh is assigned before this can finish and clear it.
        await Task.Yield();
        try
        {
            var groups = new List<GpCacheResult>();
            foreach (string group in settings.Groups)
            {
                groups.Add(await cache.GetGroupAsync(group, lifetime.ApplicationStopping).ConfigureAwait(false));
            }

            lock (_sync)
            {
                // Stamped when the data arrived, not when the load began: a load that waited on
                // another process's cache lock is still fresh for a full minute.
                _memory = (_time.GetUtcNow(), groups);
            }

            return groups;
        }
        finally
        {
            lock (_sync)
            {
                _refresh = null;
            }
        }
    }

    public async Task<IReadOnlyList<SatelliteSummary>> SatellitesAsync(CancellationToken cancellationToken)
    {
        var groups = await GroupsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        // The same choice FindAsync makes: the first group holding the satellite, newest epoch there.
        return groups
            .SelectMany((g, order) => g.Records.Select(r => (Order: order, Group: g.Group, Record: r)))
            .GroupBy(x => x.Record.Elements.CatalogNumber)
            .Select(g => g.Where(x => x.Order == g.Min(y => y.Order)).MaxBy(x => x.Record.Elements.Epoch))
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
        // Snapped to the millisecond the response reports, so every value belongs to that instant.
        DateTimeOffset t = Millisecond(at ?? _time.GetUtcNow());
        var (record, warnings) = await FindAsync(id, t, cancellationToken).ConfigureAwait(false);
        if (at is not null && (t - record.Elements.Epoch).Duration() > MaximumAtDistance)
        {
            throw new ApiException(400, "Invalid time", $"at must be within {MaximumAtDistance.TotalDays:F0} days of the element set's epoch, {record.Elements.Epoch:yyyy-MM-dd HH:mm} UTC.");
        }

        Sgp4Propagator propagator = CreatePropagator(record);
        EcefState ecef = Propagate(propagator, t);

        Geodetic subpoint = Wgs84.FromEcef(ecef.Position);
        LookAngles look = _observer.LookAt(ecef);
        Vec3 sun = Sun.PositionEcef(t);
        double sunElevation = _observer.LookAt(new EcefState(sun, default)).ElevationDegrees;
        double footprint = FootprintRadiusDegrees(subpoint.HeightKm, 0.0);
        double visibility = FootprintRadiusDegrees(subpoint.HeightKm, settings.MinimumElevationDegrees);
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
            visibility,
            current is null ? null : Pass(current, propagator),
            warnings);
    }

    public async Task<TrackResponse> TrackAsync(long id, double? minutes, CancellationToken cancellationToken)
    {
        DateTimeOffset t = Millisecond(_time.GetUtcNow());
        var (record, warnings) = await FindAsync(id, t, cancellationToken).ConfigureAwait(false);
        Sgp4Propagator propagator = CreatePropagator(record);

        // One orbit either side by default, so the map shows where the satellite has been and where
        // it is going; long periods are thinned to keep the response small.
        double span = minutes ?? PeriodMinutes(record);
        if (!(span > 0) || span > 2880)
        {
            throw new ApiException(400, "Invalid track length", "minutes must be more than 0 and at most 2880.");
        }

        TimeSpan half = TimeSpan.FromMinutes(span);
        TimeSpan step = TrackStep;
        if ((half * 2) / step > MaximumTrackPoints - 1)
        {
            // Thinned to at most MaximumTrackPoints points, on whole milliseconds.
            step = TimeSpan.FromMilliseconds(Math.Ceiling((half * 2).TotalMilliseconds / (MaximumTrackPoints - 1)));
        }

        // Sampled on whole milliseconds, so each point's values belong exactly to the time it reports.
        var points = new List<TrackPoint>();
        for (DateTimeOffset s = Millisecond(t - half); s <= t + half; s += step)
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

        DateTimeOffset t = Millisecond(_time.GetUtcNow());
        var (record, warnings) = await FindAsync(id, t, cancellationToken).ConfigureAwait(false);
        Sgp4Propagator propagator = CreatePropagator(record);
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

    /// <summary>
    /// The passes as an iCalendar file: one event per visible part (or per pass), each with an alarm.
    /// </summary>
    public async Task<string> CalendarAsync(long id, int? days, bool? visibleOnly, int? alarmMinutes, CancellationToken cancellationToken)
    {
        int d = days ?? 7;
        int alarm = alarmMinutes ?? 10;
        if (d is < 1 or > 10)
        {
            throw new ApiException(400, "Invalid number of days", "days must be from 1 to 10.");
        }

        if (alarm is < 0 or > 120)
        {
            throw new ApiException(400, "Invalid alarm", "alarm must be from 0 to 120 minutes.");
        }

        DateTimeOffset t = Millisecond(_time.GetUtcNow());
        var (record, warnings) = await FindAsync(id, t, cancellationToken).ConfigureAwait(false);
        Sgp4Propagator propagator = CreatePropagator(record);
        string name = record.Name ?? $"NORAD {id}";
        PassSearchResult search = PassFinder.Find(propagator, _observer, t, t.AddDays(d), settings.MinimumElevationDegrees);

        // The file outlives the page, so every event carries the caveats the dashboard shows.
        var caveats = new List<string> { string.Create(CultureInfo.InvariantCulture, $"Predicted from elements with epoch {record.Elements.Epoch.UtcDateTime:yyyy-MM-dd HH:mm} UTC, on {Utc(t):yyyy-MM-dd HH:mm} UTC.") };
        caveats.AddRange(warnings);
        if (search.StoppedAt is { } stoppedAt)
        {
            caveats.Add(string.Create(CultureInfo.InvariantCulture, $"SGP4 stopped at {stoppedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC ({search.StoppedBy}); later passes are not included."));
        }

        var events = new List<CalendarEvent>();
        foreach (SatellitePass pass in search.Passes)
        {
            var windows = Visibility.Windows(pass, propagator, _observer);
            string key = PassKey(record, pass, propagator);
            string details = string.Join("\n", [
                $"Rises {Describe(pass.Rise)}",
                $"Peak {Describe(pass.Culmination)}",
                $"Sets {Describe(pass.Set)}",
                $"Observer: {settings.ObserverName}",
                "Visible means sunlit, with the Sun below -6° at the observer.",
                .. caveats,
            ]);
            if (visibleOnly ?? true)
            {
                for (int k = 0; k < windows.Count; k++)
                {
                    VisibleWindow w = windows[k];
                    events.Add(new CalendarEvent(
                        w.Start.Time,
                        w.End.Time,
                        string.Create(CultureInfo.InvariantCulture, $"{name} visible, up to {w.Highest.ElevationDegrees:F0}°"),
                        $"Visible from {Describe(w.Start)} to {Describe(w.End)}, {Ending(w.EndsBecause)}.\n{details}",
                        $"{key}-v{k}"));
                }
            }
            else
            {
                events.Add(new CalendarEvent(
                    pass.Rise.Time,
                    pass.Set.Time,
                    string.Create(CultureInfo.InvariantCulture, $"{name} pass, up to {pass.Culmination.ElevationDegrees:F0}°{(windows.Count > 0 ? ", visible" : string.Empty)}"),
                    details,
                    key));
            }
        }

        if (events.Count == 0)
        {
            throw new ApiException(404, "No passes to export", (visibleOnly ?? true)
                ? $"{name} has no visible passes in the next {d} days, so there is nothing to add to a calendar."
                : $"{name} has no passes in the next {d} days.");
        }

        return Calendar.Write($"{name} passes over {settings.ObserverName}", events, t, alarm);

        // Times rounded to the second as DTSTART and DTEND are, and azimuths shown as 0 to 359.
        string Describe(PassEvent e) => string.Create(
            CultureInfo.InvariantCulture,
            $"{Local(e.Time)} at {e.ElevationDegrees:F0}° elevation, {Compass(e.AzimuthDegrees)} ({(int)Math.Round(e.AzimuthDegrees) % 360}°)");

        string Local(DateTimeOffset instant)
        {
            DateTimeOffset local = TimeZoneInfo.ConvertTime(Calendar.RoundToSecond(instant), settings.TimeZone);
            TimeSpan offset = local.Offset;
            return string.Create(CultureInfo.InvariantCulture, $"{local:yyyy-MM-dd HH:mm:ss} UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}");
        }
    }

    /// <summary>
    /// What identifies a pass across element sets, for calendar UIDs: the revolution number at the
    /// culmination. New elements move a pass by seconds, which a key from its clock time can turn
    /// into a different minute; the revolution number changes only at the ascending node, and a
    /// pass seen from mid-latitudes peaks far from the node. It is the revolution number at the
    /// epoch (REV_AT_EPOCH) plus the node crossings since: the accumulated argument of latitude,
    /// estimated from the mean elements (good to tens of degrees over days), less the true argument
    /// of latitude at the culmination, in whole turns. Without a revolution number, or for an
    /// equatorial orbit with no defined node, the key falls back to the minute of the culmination.
    /// An element set whose epoch lies within a fraction of a degree of the node can count its
    /// revolution differently from these mean elements, and so shift every key by one.
    /// </summary>
    internal static string PassKey(GpRecord record, SatellitePass pass, Sgp4Propagator propagator)
    {
        long id = record.Elements.CatalogNumber;
        DateTimeOffset culmination = pass.Culmination.Time;
        PropagationResult state = propagator.Propagate(culmination);
        if (record.RevolutionAtEpoch is { } revolutionAtEpoch && state.Succeeded && ArgumentOfLatitudeDegrees(state.State) is { } u)
        {
            MeanElements e = record.Elements;
            double u0 = ((e.ArgumentOfPericenter + e.MeanAnomaly) % 360.0 + 360.0) % 360.0;
            double turns = e.MeanMotion * (culmination - e.Epoch).TotalDays;
            long crossings = (long)Math.Round(((u0 + (360.0 * turns)) - u) / 360.0);
            return string.Create(CultureInfo.InvariantCulture, $"{id}-r{revolutionAtEpoch + crossings}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{id}-{culmination.UtcDateTime:yyyyMMdd'T'HHmm}");
    }

    /// <summary>The angle from the ascending node to the satellite in its orbit plane, degrees in [0, 360), or null for an equatorial orbit.</summary>
    internal static double? ArgumentOfLatitudeDegrees(TemeState state)
    {
        Vec3 r = state.Position;
        Vec3 v = state.Velocity;
        var h = new Vec3((r.Y * v.Z) - (r.Z * v.Y), (r.Z * v.X) - (r.X * v.Z), (r.X * v.Y) - (r.Y * v.X));
        var node = new Vec3(-h.Y, h.X, 0.0); // z × h
        if (node.Length < 1e-9 * h.Length)
        {
            return null;
        }

        Vec3 n = node * (1.0 / node.Length);
        Vec3 hUnit = h * (1.0 / h.Length);
        var inPlane = new Vec3((hUnit.Y * n.Z) - (hUnit.Z * n.Y), (hUnit.Z * n.X) - (hUnit.X * n.Z), (hUnit.X * n.Y) - (hUnit.Y * n.X)); // ĥ × n̂
        double u = Math.Atan2(r.Dot(inPlane), r.Dot(n)) * RadiansToDegrees;
        return u < 0 ? u + 360.0 : u;
    }

    /// <summary>A 16-point compass direction for an azimuth.</summary>
    internal static string Compass(double azimuthDegrees)
    {
        string[] points = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];
        int index = (int)Math.Floor((((azimuthDegrees % 360.0) + 360.0) % 360.0 / 22.5) + 0.5) % 16;
        return points[index];
    }

    private static string Ending(VisibilityChange change) => change switch
    {
        VisibilityChange.EntersShadow => "when it enters the Earth's shadow",
        VisibilityChange.SkyBrightens => "when the sky brightens",
        _ => "when it sets",
    };

    public async Task<HealthResponse> HealthAsync(CancellationToken cancellationToken)
    {
        var groups = await GroupsAsync(cancellationToken).ConfigureAwait(false);
        bool anyData = groups.Any(g => g.Records.Count > 0);
        return new HealthResponse(
            anyData ? "ok" : "no data",
            settings.Offline,
            Utc(_time.GetUtcNow()),
            groups.Select(g => new GroupHealth(g.Group, g.Records.Count, g.Source.ToString(), g.DownloadedUtc is { } dl ? Utc(dl) : null, g.Warnings)).ToList());
    }

    private PassDto Pass(SatellitePass pass, Sgp4Propagator propagator)
    {
        var windows = Visibility.Windows(pass, propagator, _observer);

        // Every 10 s from rise, plus set and each visible part's ends exactly, so the sky plot can
        // draw the visible part without interpolating.
        // Sampled on the whole milliseconds the API reports, so each point's values belong exactly
        // to its time.
        var times = new SortedSet<DateTimeOffset> { Millisecond(pass.Set.Time) };
        for (DateTimeOffset s = pass.Rise.Time; s < pass.Set.Time; s += PathStep)
        {
            times.Add(Millisecond(s));
        }

        foreach (VisibleWindow w in windows)
        {
            times.Add(Millisecond(w.Start.Time));
            times.Add(Millisecond(w.End.Time));
        }

        var path = times.Select(t => SkyPointAt(t, propagator)).ToList();
        var visible = windows
            .Select(w => new VisibleDto(Event(w.Start), Change(w.StartsBecause), Event(w.Highest), Event(w.End), Change(w.EndsBecause)))
            .ToList();
        return new PassDto(Event(pass.Rise), Event(pass.Culmination), Event(pass.Set), pass.PeakElevationUncertaintyDegrees, visible, path);
    }

    /// <summary>
    /// The Earth-central angle from the subpoint to where the satellite stands at a given elevation,
    /// on a spherical Earth of mean radius: acos(R cos e / (R + h)) − e. For the map only; passes
    /// come from the ellipsoidal core.
    /// </summary>
    internal static double FootprintRadiusDegrees(double heightKm, double elevationDegrees)
    {
        double e = elevationDegrees / RadiansToDegrees;
        double ratio = MeanEarthRadiusKm * Math.Cos(e) / (MeanEarthRadiusKm + Math.Max(heightKm, 0));
        return (Math.Acos(Math.Min(1.0, ratio)) - e) * RadiansToDegrees;
    }

    private SkyPoint SkyPointAt(DateTimeOffset t, Sgp4Propagator propagator)
    {
        EcefState ecef = Propagate(propagator, t);
        LookAngles look = _observer.LookAt(ecef);
        return new SkyPoint(Utc(t), look.AzimuthDegrees, look.ElevationDegrees, EarthShadow.IsSunlit(ecef.Position, Sun.PositionEcef(t)));
    }

    /// <summary>
    /// The element set the CLI would use: the configured groups in order, the first that holds
    /// the satellite, and its newest epoch there.
    /// </summary>
    private async Task<(GpRecord Record, IReadOnlyList<string> Warnings)> FindAsync(long id, DateTimeOffset t, CancellationToken cancellationToken)
    {
        var groups = await GroupsAsync(cancellationToken).ConfigureAwait(false);
        foreach (GpCacheResult group in groups)
        {
            GpRecord? record = group.Records.Where(r => r.Elements.CatalogNumber == id).MaxBy(r => r.Elements.Epoch);
            if (record is null)
            {
                continue;
            }

            var warnings = new List<string>(group.Warnings);
            // Age at the instant the response is for, which ?at= can move away from the clock.
            double age = (t - record.Elements.Epoch).TotalDays;
            if (age > 3)
            {
                warnings.Add($"{record.Name} elements are {age:F1} days old; predictions degrade by kilometers per day of element age.");
            }
            else if (age < -1.0 / 1440)
            {
                // Elements from after "now": the clock is simulated or wrong. SGP4 propagates
                // backward as well as forward, so the predictions stand, but it is worth knowing.
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{record.Name} elements are from {-age * 24:F1} hours after the requested time; is the clock simulated or wrong?"));
            }

            return (record, warnings);
        }

        if (groups.All(g => g.Records.Count == 0))
        {
            throw new ApiException(503, "No orbital data", "No element sets are available. " + string.Join(" ", groups.SelectMany(g => g.Warnings)));
        }

        throw new ApiException(404, "Unknown satellite", $"NORAD {id} is not in {string.Join(", ", groups.Where(g => g.Records.Count > 0).Select(g => g.Group))}.");
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

    /// <summary>An instant truncated to the millisecond.</summary>
    private static DateTimeOffset Millisecond(DateTimeOffset t) => t.AddTicks(-(t.UtcTicks % TimeSpan.TicksPerMillisecond));

    /// <summary>An instant as a UTC DateTime truncated to the millisecond, which JSON writes with a Z.</summary>
    internal static DateTime Utc(DateTimeOffset instant)
    {
        DateTime utc = instant.UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }
}
