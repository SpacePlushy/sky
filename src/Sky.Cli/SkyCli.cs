using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Sky.CelesTrak;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Cli;

/// <summary>The sky command-line tool: current position and upcoming passes for one satellite.</summary>
internal static class SkyCli
{
    public static async Task<int> RunAsync(string[] args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var satellite = new Option<long>("--sat") { Description = "NORAD catalog number.", DefaultValueFactory = _ => 25544 };
        var refresh = new Option<bool>("--refresh") { Description = "Download element sets now, if CelesTrak's 2-hour rule allows." };
        var count = new Option<int>("--count", "-n") { Description = "How many passes to list.", DefaultValueFactory = _ => 5 };
        var days = new Option<int>("--days") { Description = "How many days ahead to search.", DefaultValueFactory = _ => 7 };
        var minimumElevation = new Option<double?>("--min-elevation")
        {
            Description = "Minimum elevation in degrees, from 0 to below 90. Defaults to the Passes:MinimumElevationDegrees setting.",
            CustomParser = ParseElevation,
        };
        var visibleOnly = new Option<bool>("--visible") { Description = "List only passes with a visible part: satellite sunlit, Sun below -6° at the observer." };
        var group = new Argument<string>("group") { Description = "CelesTrak group, such as stations." };

        var now = new Command("now", "Where the satellite is now, as seen from the observer.") { satellite, refresh };
        now.SetAction((parse, token) => ExecuteAsync(environment, context =>
            NowAsync(context, parse.GetValue(satellite), parse.GetValue(refresh), token)));

        var passes = new Command("passes", "The satellite's next passes over the observer, and which parts of them can be seen.") { satellite, refresh, count, days, minimumElevation, visibleOnly };
        passes.SetAction((parse, token) => ExecuteAsync(environment, context =>
            PassesAsync(context, new PassesRequest(parse.GetValue(satellite), parse.GetValue(refresh), parse.GetValue(count), parse.GetValue(days), parse.GetValue(minimumElevation), parse.GetValue(visibleOnly)), token)));

        var unblock = new Command("unblock", "Allow requests to a CelesTrak group again, after checking why it was blocked.") { group };
        unblock.SetAction((parse, _) => ExecuteAsync(environment, context =>
        {
            string name = parse.GetValue(group)!;
            try
            {
                context.Cache.ClearBlock(name);
            }
            catch (ArgumentException ex)
            {
                environment.Error.WriteLine(ex.Message);
                return Task.FromResult(1);
            }

            environment.Out.WriteLine($"Requests for GROUP={name} are allowed again, subject to the 2-hour rule.");
            return Task.FromResult(0);
        }));

        var root = new RootCommand("Sky: satellite position, passes, and visibility over an observer.") { now, passes, unblock };
        return await root.Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = environment.Out, Error = environment.Error },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses --min-elevation with the invariant culture, so 30.5 means the same everywhere.</summary>
    private static double? ParseElevation(ArgumentResult result)
    {
        string token = result.Tokens.Count == 1 ? result.Tokens[0].Value : string.Empty;
        if (double.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)
            && value >= 0 && value < 90)
        {
            return value;
        }

        result.AddError($"--min-elevation must be a number of degrees from 0 to below 90, such as 30.5; got \"{token}\".");
        return null;
    }

    private static async Task<int> ExecuteAsync(CliEnvironment environment, Func<CommandContext, Task<int>> command)
    {
        SkySettings settings;
        try
        {
            settings = SkySettings.Load(environment.SettingsDirectory, environment.EnvironmentPrefix);
        }
        catch (SettingsException ex)
        {
            await environment.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var http = environment.CreateHttpClient();
        using var cache = new GpCache(settings.CacheDirectory, new CelesTrakClient(http), environment.Time);
        return await command(new CommandContext(environment, settings, cache)).ConfigureAwait(false);
    }

    private static async Task<int> NowAsync(CommandContext context, long catalogNumber, bool refresh, CancellationToken token)
    {
        GpRecord? record = await FindAsync(context, catalogNumber, refresh, token).ConfigureAwait(false);
        if (record is null || !TryCreatePropagator(context, record, out Sgp4Propagator? propagator))
        {
            return 1;
        }

        var (env, settings, _) = context;
        DateTimeOffset t = env.Time.GetUtcNow();
        PropagationResult result = propagator.Propagate(t);
        if (!result.Succeeded)
        {
            await env.Error.WriteLineAsync($"SGP4 cannot propagate NORAD {catalogNumber} to {Format.Utc(t)} UTC: {result.Error}.").ConfigureAwait(false);
            return 1;
        }

        EcefState ecef = EarthRotation.TemeToEcef(result.State, t);
        Geodetic subpoint = Wgs84.FromEcef(ecef.Position);
        var frame = new TopocentricFrame(settings.Observer);
        LookAngles look = frame.LookAt(ecef);
        bool sunlit = Visibility.IsSunlit(propagator, t);
        double sunElevation = Visibility.SunElevationDegrees(frame, t);
        PassSearchResult upcoming = PassFinder.Find(propagator, frame, t, t.AddDays(7), settings.MinimumElevationDegrees);
        SatellitePass? current = upcoming.Passes.FirstOrDefault(p => p.Rise.Time <= t && t <= p.Set.Time);
        SatellitePass? next = upcoming.Passes.FirstOrDefault(p => p.Rise.Time > t);
        (SatellitePass Pass, VisibleWindow Window)? nextVisible = upcoming.Passes
            .SelectMany(p => Visibility.Windows(p, propagator, frame).Select(w => (Pass: p, Window: w)))
            .Where(v => v.Window.End.Time > t)
            .Select(v => ((SatellitePass Pass, VisibleWindow Window)?)v)
            .FirstOrDefault();
        TimeZoneInfo zone = settings.TimeZone;

        TextWriter o = env.Out;
        await o.WriteLineAsync($"{record.Name}  NORAD {catalogNumber}").ConfigureAwait(false);
        await o.WriteLineAsync($"  time        {Format.LocalTime(t, zone)} {zone.Id} ({Format.Utc(t)} UTC)").ConfigureAwait(false);
        await o.WriteLineAsync($"  elements    epoch {Format.Utc(record.Elements.Epoch)} UTC, {Format.Number((t - record.Elements.Epoch).TotalDays, 1)} days old").ConfigureAwait(false);
        await o.WriteLineAsync($"  latitude    {Format.Number(subpoint.LatitudeDegrees, 4)}°").ConfigureAwait(false);
        await o.WriteLineAsync($"  longitude   {Format.Number(subpoint.LongitudeDegrees, 4)}°").ConfigureAwait(false);
        await o.WriteLineAsync($"  altitude    {Format.Number(subpoint.HeightKm, 1)} km").ConfigureAwait(false);
        await o.WriteLineAsync($"  speed       {Format.Number(result.State.Velocity.Length, 3)} km/s (inertial)").ConfigureAwait(false);
        await o.WriteLineAsync($"  sunlight    {(sunlit ? "sunlit" : "in the Earth's shadow")}").ConfigureAwait(false);
        await o.WriteLineAsync($"From {settings.ObserverName} ({Format.Number(settings.Observer.LatitudeDegrees, 4)}°, {Format.Number(settings.Observer.LongitudeDegrees, 4)}°, {Format.Number(settings.Observer.HeightKm * 1000, 0)} m)").ConfigureAwait(false);
        await o.WriteLineAsync($"  azimuth     {Format.Number(look.AzimuthDegrees, 2)}°").ConfigureAwait(false);
        await o.WriteLineAsync($"  elevation   {Format.Number(look.ElevationDegrees, 2)}°{(look.ElevationDegrees < 0 ? " (below the horizon)" : string.Empty)}").ConfigureAwait(false);
        await o.WriteLineAsync($"  range       {Format.Number(look.RangeKm, 2)} km").ConfigureAwait(false);
        await o.WriteLineAsync($"  range rate  {Format.Number(look.RangeRateKmPerSecond, 3)} km/s").ConfigureAwait(false);
        await o.WriteLineAsync($"  sun         {Format.Number(sunElevation, 1)}° ({(sunElevation < Visibility.TwilightSunElevationDegrees ? "dark enough to see satellites" : "too bright to see satellites")})").ConfigureAwait(false);
        if (current is not null)
        {
            await o.WriteLineAsync($"  pass        in progress: rose {Format.LocalClock(Format.RoundToSecond(current.Rise.Time), zone)}, peak {Format.Number(current.Culmination.ElevationDegrees, 1)}° at {Format.LocalClock(Format.RoundToSecond(current.Culmination.Time), zone)}, sets {Format.LocalClock(Format.RoundToSecond(current.Set.Time), zone)}").ConfigureAwait(false);
        }
        bool upForDays = current is null && upcoming.AboveMinimumAtStartSince is not null;
        if (upForDays)
        {
            // Up for longer than the finder follows a pass, so there is no rise to report; the set may be.
            await o.WriteLineAsync(upcoming.SetOfPassUpAtStart is { } setsAt
                ? $"  pass        above {Format.Degrees(settings.MinimumElevationDegrees)}° for more than a day; sets {Format.LocalTime(Format.RoundToSecond(setsAt.Time), zone)}"
                : $"  pass        above {Format.Degrees(settings.MinimumElevationDegrees)}° for more than a day; no rise or set to report").ConfigureAwait(false);
        }

        string? stop = Format.SearchStop(upcoming, zone);
        if (next is not null)
        {
            await o.WriteLineAsync($"  next pass   rises {Format.LocalTime(Format.RoundToSecond(next.Rise.Time), zone)} ({Format.Countdown(next.Rise.Time - t)}), peaks at {Format.Number(next.Culmination.ElevationDegrees, 1)}°").ConfigureAwait(false);
        }
        else if (upcoming.RiseOfPassUpAtEnd is { } longRise && longRise.Time > t)
        {
            await o.WriteLineAsync($"  next pass   rises {Format.LocalTime(Format.RoundToSecond(longRise.Time), zone)} ({Format.Countdown(longRise.Time - t)}) and stays above {Format.Degrees(settings.MinimumElevationDegrees)}° for more than a day").ConfigureAwait(false);
        }
        else if (current is null && !upForDays)
        {
            await o.WriteLineAsync(stop is not null
                ? $"  next pass   none found: {stop}"
                : $"  next pass   none above {Format.Degrees(settings.MinimumElevationDegrees)}° in the next 7 days").ConfigureAwait(false);
        }

        if (stop is not null && next is not null)
        {
            await o.WriteLineAsync($"  search      {stop}").ConfigureAwait(false);
        }

        // Visible parts are computed for complete passes. For a satellite up for days, say whether
        // it can be seen right now.
        bool visibleNow = sunlit && sunElevation < Visibility.TwilightSunElevationDegrees && look.ElevationDegrees >= settings.MinimumElevationDegrees;
        string visibleLine = nextVisible is { } v
            ? $"  visible     {(v.Window.Start.Time <= t ? "now" : Format.LocalTime(Format.RoundToSecond(v.Window.Start.Time), zone))} until {Format.LocalClock(Format.RoundToSecond(v.Window.End.Time), zone)}, up to {Format.Number(v.Window.Highest.ElevationDegrees, 1)}°"
            : upForDays
                ? $"  visible     {(visibleNow ? "now" : "not now")} (up for more than a day, so visible parts are not predicted)"
                : stop is not null
                    ? $"  visible     none found: {stop}"
                    : "  visible     no visible pass in the next 7 days";
        await o.WriteLineAsync(visibleLine).ConfigureAwait(false);

        return 0;
    }

    private static async Task<int> PassesAsync(CommandContext context, PassesRequest request, CancellationToken token)
    {
        var (env, settings, _) = context;
        if (request.Count < 1 || request.Days < 1 || request.Days > 30)
        {
            await env.Error.WriteLineAsync("--count must be at least 1, and --days from 1 to 30.").ConfigureAwait(false);
            return 1;
        }

        GpRecord? record = await FindAsync(context, request.Satellite, request.Refresh, token).ConfigureAwait(false);
        if (record is null || !TryCreatePropagator(context, record, out Sgp4Propagator? propagator))
        {
            return 1;
        }

        double minimum = request.MinimumElevation ?? settings.MinimumElevationDegrees;
        DateTimeOffset t = env.Time.GetUtcNow();
        DateTimeOffset end = t.AddDays(request.Days);
        var frame = new TopocentricFrame(settings.Observer);
        PassSearchResult search = PassFinder.Find(propagator, frame, t, end, minimum);
        var rows = search.Passes
            .Select(p => (Pass: p, Windows: Visibility.Windows(p, propagator, frame)))
            .Where(r => !request.VisibleOnly || r.Windows.Count > 0)
            .Take(request.Count)
            .ToList();
        TimeZoneInfo zone = settings.TimeZone;

        TextWriter o = env.Out;
        await o.WriteLineAsync($"{record.Name}  NORAD {request.Satellite}, elements from {Format.Utc(record.Elements.Epoch)} UTC ({Format.Number((t - record.Elements.Epoch).TotalDays, 1)} days old)").ConfigureAwait(false);
        string offsetLabel = Format.OffsetLabel(zone, t, end);
        bool offsetChanges = offsetLabel.Contains("daylight saving", StringComparison.Ordinal);
        string which = request.VisibleOnly ? "Visible passes" : "Passes";
        await o.WriteLineAsync($"{which} over {settings.ObserverName} above {Format.Degrees(minimum)}° in the next {request.Days} days. Times in {zone.Id} ({offsetLabel}{(offsetChanges ? "; each time shows its offset" : string.Empty)}).").ConfigureAwait(false);
        string peakBound = rows.Count > 0 ? $"{Format.BoundDegrees(rows.Max(r => r.Pass.PeakElevationUncertaintyDegrees))}°" : "a per-pass bound";
        await o.WriteLineAsync($"Rise, peak, and set are found to 1 ms and printed to the nearest second; peak elevation within {peakBound}. Geometric elevation, no refraction.").ConfigureAwait(false);
        await o.WriteLineAsync("Visible: the satellite is sunlit and the Sun is below -6° at the observer.").ConfigureAwait(false);
        await o.WriteLineAsync().ConfigureAwait(false);
        await o.WriteLineAsync("  Rise                 Az       Peak       El      Az       Set       Az      Visible").ConfigureAwait(false);
        foreach (var (pass, windows) in rows)
        {
            // Where daylight saving changes the offset inside the window, a wall-clock time alone can
            // be ambiguous (the repeated hour), so each time then carries its offset.
            DateTimeOffset rise = Format.RoundToSecond(pass.Rise.Time);
            DateTimeOffset peak = Format.RoundToSecond(pass.Culmination.Time);
            DateTimeOffset set = Format.RoundToSecond(pass.Set.Time);
            // Each time is rounded once, and its clock and offset both come from the rounded instant,
            // so a time near a daylight saving change cannot print with the other side's offset.
            string visible = windows.Count == 0
                ? "no"
                : string.Join("; ", windows.Select(w =>
                {
                    DateTimeOffset from = Format.RoundToSecond(w.Start.Time);
                    DateTimeOffset to = Format.RoundToSecond(w.End.Time);
                    return $"{Format.LocalClock(from, zone)}{Offset(from)}-{Format.LocalClock(to, zone)}{Offset(to)} up to {Format.Number(w.Highest.ElevationDegrees, 1)}°{Ending(w.EndsBecause)}";
                }));
            await o.WriteLineAsync(
                $"  {Format.LocalTime(rise, zone)}{Offset(rise)}  {Azimuth(pass.Rise.AzimuthDegrees)}  " +
                $"{Format.LocalClock(peak, zone)}{Offset(peak)}  {Format.Number(pass.Culmination.ElevationDegrees, 1),5}°  {Azimuth(pass.Culmination.AzimuthDegrees)}  " +
                $"{Format.LocalClock(set, zone)}{Offset(set)}  {Azimuth(pass.Set.AzimuthDegrees)}  {visible}{(pass.Rise.Time < t ? "  (in progress)" : string.Empty)}").ConfigureAwait(false);
        }

        if (search.AboveMinimumAtStartSince is not null)
        {
            await o.WriteLineAsync(search.SetOfPassUpAtStart is { } setsAt
                ? $"  NORAD {request.Satellite} has been above {Format.Degrees(minimum)}° for more than a day and sets {Format.LocalTime(Format.RoundToSecond(setsAt.Time), zone)}{Offset(Format.RoundToSecond(setsAt.Time))}; that pass has no rise to list."
                : $"  NORAD {request.Satellite} has been above {Format.Degrees(minimum)}° for more than a day, so that pass has no rise or set to list.").ConfigureAwait(false);
        }

        if (search.RiseOfPassUpAtEnd is { } longRise && search.AboveMinimumAtEndUntil is not null)
        {
            await o.WriteLineAsync($"  NORAD {request.Satellite} rises {Format.LocalTime(Format.RoundToSecond(longRise.Time), zone)}{Offset(Format.RoundToSecond(longRise.Time))} and stays above {Format.Degrees(minimum)}° for more than a day, so that pass has no set to list.").ConfigureAwait(false);
        }

        if (rows.Count < request.Count)
        {
            string found = $"Only {rows.Count} of {request.Count} {(request.VisibleOnly ? "visible " : string.Empty)}passes found";
            await o.WriteLineAsync(Format.SearchStop(search, zone) is { } stop
                ? $"  {found}. {stop}"
                : $"  {found} in the next {request.Days} days.").ConfigureAwait(false);
        }

        return 0;

        string Offset(DateTimeOffset instant) => offsetChanges ? Format.OffsetSuffix(instant, zone) : string.Empty;
    }

    private static string Ending(VisibilityChange change) => change switch
    {
        VisibilityChange.EntersShadow => ", then into shadow",
        VisibilityChange.SkyBrightens => ", then the sky brightens",
        _ => string.Empty,
    };

    private static string Azimuth(double degrees) => $"{Format.Number(degrees, 1),5}°";

    /// <summary>Finds the satellite's newest element set, checking groups in order and stopping at the first that has it.</summary>
    private static async Task<GpRecord?> FindAsync(CommandContext context, long catalogNumber, bool refresh, CancellationToken token)
    {
        var (env, settings, cache) = context;
        var loaded = new List<string>();
        var unavailable = new List<string>();
        foreach (string group in settings.Groups)
        {
            GpCacheResult result = await cache.GetGroupAsync(group, token, refresh).ConfigureAwait(false);
            foreach (string warning in result.Warnings)
            {
                await env.Error.WriteLineAsync($"warning: {warning}").ConfigureAwait(false);
            }

            (result.Records.Count > 0 ? loaded : unavailable).Add(group);
            GpRecord? match = result.Records.Where(r => r.Elements.CatalogNumber == catalogNumber).MaxBy(r => r.Elements.Epoch);
            if (match is not null)
            {
                TimeSpan age = env.Time.GetUtcNow() - match.Elements.Epoch;
                if (age > TimeSpan.FromDays(3))
                {
                    await env.Error.WriteLineAsync($"warning: {match.Name} elements are {Format.Number(age.TotalDays, 1)} days old; predictions degrade by kilometers per day of element age.").ConfigureAwait(false);
                }

                return match;
            }
        }

        string missing = unavailable.Count == 0
            ? string.Empty
            : $" {string.Join(", ", unavailable)} could not be loaded; see the warnings above.";
        await env.Error.WriteLineAsync(loaded.Count > 0
            ? $"NORAD {catalogNumber} is not in {string.Join(", ", loaded)}.{missing}"
            : "No element sets are available: nothing is cached and CelesTrak could not be used. See the warnings above.").ConfigureAwait(false);
        return null;
    }

    private static bool TryCreatePropagator(CommandContext context, GpRecord record, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Sgp4Propagator? propagator)
    {
        try
        {
            propagator = Sgp4Propagator.Create(record.ToSgp4Elements());
            return true;
        }
        catch (InvalidOperationException ex)
        {
            context.Environment.Error.WriteLine(ex.Message);
            propagator = null;
            return false;
        }
    }

    private sealed record CommandContext(CliEnvironment Environment, SkySettings Settings, GpCache Cache);

    private sealed record PassesRequest(long Satellite, bool Refresh, int Count, int Days, double? MinimumElevation, bool VisibleOnly);
}
