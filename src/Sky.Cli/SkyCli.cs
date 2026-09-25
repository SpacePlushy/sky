using System.CommandLine;
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
        var minimumElevation = new Option<double?>("--min-elevation") { Description = "Minimum elevation in degrees. Defaults to the Passes:MinimumElevationDegrees setting." };
        var group = new Argument<string>("group") { Description = "CelesTrak group, such as stations." };

        var now = new Command("now", "Where the satellite is now, as seen from the observer.") { satellite, refresh };
        now.SetAction((parse, token) => ExecuteAsync(environment, context =>
            NowAsync(context, parse.GetValue(satellite), parse.GetValue(refresh), token)));

        var passes = new Command("passes", "The satellite's next passes over the observer.") { satellite, refresh, count, days, minimumElevation };
        passes.SetAction((parse, token) => ExecuteAsync(environment, context =>
            PassesAsync(context, parse.GetValue(satellite), parse.GetValue(refresh), parse.GetValue(count), parse.GetValue(days), parse.GetValue(minimumElevation), token)));

        var unblock = new Command("unblock", "Allow requests to a CelesTrak group again, after checking why it was blocked.") { group };
        unblock.SetAction((parse, _) => ExecuteAsync(environment, context =>
        {
            context.Cache.ClearBlock(parse.GetValue(group)!);
            environment.Out.WriteLine($"Requests for GROUP={parse.GetValue(group)} are allowed again, subject to the 2-hour rule.");
            return Task.FromResult(0);
        }));

        var root = new RootCommand("Sky: satellite position and passes over an observer (Milestone 1).") { now, passes, unblock };
        return await root.Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = environment.Out, Error = environment.Error },
            cancellationToken).ConfigureAwait(false);
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
        PassSearchResult upcoming = CoarsePassFinder.Find(propagator, frame, t, t.AddDays(7), settings.MinimumElevationDegrees);
        SatellitePass? next = upcoming.Passes.Count > 0 ? upcoming.Passes[0] : null;

        TextWriter o = env.Out;
        await o.WriteLineAsync($"{record.Name}  NORAD {catalogNumber}").ConfigureAwait(false);
        await o.WriteLineAsync($"  time        {Format.LocalTime(t, settings.TimeZone)} {settings.TimeZone.Id} ({Format.Utc(t)} UTC)").ConfigureAwait(false);
        await o.WriteLineAsync($"  elements    epoch {Format.Utc(record.Elements.Epoch)} UTC, {Format.Number((t - record.Elements.Epoch).TotalDays, 1)} days old").ConfigureAwait(false);
        await o.WriteLineAsync($"  latitude    {Format.Number(subpoint.LatitudeDegrees, 4)}°").ConfigureAwait(false);
        await o.WriteLineAsync($"  longitude   {Format.Number(subpoint.LongitudeDegrees, 4)}°").ConfigureAwait(false);
        await o.WriteLineAsync($"  altitude    {Format.Number(subpoint.HeightKm, 1)} km").ConfigureAwait(false);
        await o.WriteLineAsync($"  speed       {Format.Number(result.State.Velocity.Length, 3)} km/s (inertial)").ConfigureAwait(false);
        await o.WriteLineAsync($"From {settings.ObserverName} ({Format.Number(settings.Observer.LatitudeDegrees, 4)}°, {Format.Number(settings.Observer.LongitudeDegrees, 4)}°, {Format.Number(settings.Observer.HeightKm * 1000, 0)} m)").ConfigureAwait(false);
        await o.WriteLineAsync($"  azimuth     {Format.Number(look.AzimuthDegrees, 2)}°").ConfigureAwait(false);
        await o.WriteLineAsync($"  elevation   {Format.Number(look.ElevationDegrees, 2)}°{(look.ElevationDegrees < 0 ? " (below the horizon)" : string.Empty)}").ConfigureAwait(false);
        await o.WriteLineAsync($"  range       {Format.Number(look.RangeKm, 2)} km").ConfigureAwait(false);
        await o.WriteLineAsync($"  range rate  {Format.Number(look.RangeRateKmPerSecond, 3)} km/s").ConfigureAwait(false);
        if (look.ElevationDegrees >= settings.MinimumElevationDegrees)
        {
            // The pass finder only reports passes that rise inside its window, so say so directly.
            await o.WriteLineAsync($"  pass        in progress now, above {Format.Number(settings.MinimumElevationDegrees, 0)}°").ConfigureAwait(false);
        }
        else if (next is not null)
        {
            await o.WriteLineAsync($"  next pass   rises {Format.LocalTime(next.Rise.Time, settings.TimeZone)} ({Format.Countdown(next.Rise.Time - t)}), peaks at {Format.Number(next.Culmination.ElevationDegrees, 1)}°").ConfigureAwait(false);
        }
        else
        {
            await o.WriteLineAsync(Format.SearchStop(upcoming, settings.TimeZone) is { } stop
                ? $"  next pass   none found: {stop}"
                : $"  next pass   none above {Format.Number(settings.MinimumElevationDegrees, 0)}° in the next 7 days").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> PassesAsync(
        CommandContext context, long catalogNumber, bool refresh, int count, int days, double? minimumElevation, CancellationToken token)
    {
        var (env, settings, _) = context;
        if (count < 1 || days < 1 || days > 30 || minimumElevation is < 0 or >= 90)
        {
            await env.Error.WriteLineAsync("--count must be at least 1, --days from 1 to 30, and --min-elevation from 0 to below 90.").ConfigureAwait(false);
            return 1;
        }

        GpRecord? record = await FindAsync(context, catalogNumber, refresh, token).ConfigureAwait(false);
        if (record is null || !TryCreatePropagator(context, record, out Sgp4Propagator? propagator))
        {
            return 1;
        }

        double minimum = minimumElevation ?? settings.MinimumElevationDegrees;
        DateTimeOffset t = env.Time.GetUtcNow();
        PassSearchResult search = CoarsePassFinder.Find(propagator, new TopocentricFrame(settings.Observer), t, t.AddDays(days), minimum);
        var found = search.Passes.Take(count).ToList();
        TimeZoneInfo zone = settings.TimeZone;

        TextWriter o = env.Out;
        await o.WriteLineAsync($"{record.Name}  NORAD {catalogNumber}, elements from {Format.Utc(record.Elements.Epoch)} UTC ({Format.Number((t - record.Elements.Epoch).TotalDays, 1)} days old)").ConfigureAwait(false);
        await o.WriteLineAsync($"Passes over {settings.ObserverName} above {Format.Number(minimum, 0)}° in the next {days} days. Times in {zone.Id} ({Format.OffsetLabel(zone, t, t.AddDays(days))}).").ConfigureAwait(false);
        await o.WriteLineAsync("Milestone 1 accuracy: rise and set within 10 s; peak within 0.1 s and 0.06°. Geometric elevation, no refraction. A pass already in progress is not listed.").ConfigureAwait(false);
        await o.WriteLineAsync().ConfigureAwait(false);
        await o.WriteLineAsync("  Rise                 Az       Peak      El      Az       Set       Az").ConfigureAwait(false);
        foreach (SatellitePass pass in found)
        {
            await o.WriteLineAsync(
                $"  {Format.LocalTime(pass.Rise.Time, zone)}  {Azimuth(pass.Rise.AzimuthDegrees)}  " +
                $"{Format.LocalClock(pass.Culmination.Time, zone)}  {Format.Number(pass.Culmination.ElevationDegrees, 1),5}°  {Azimuth(pass.Culmination.AzimuthDegrees)}  " +
                $"{Format.LocalClock(pass.Set.Time, zone)}  {Azimuth(pass.Set.AzimuthDegrees)}").ConfigureAwait(false);
        }

        if (found.Count < count)
        {
            await o.WriteLineAsync(Format.SearchStop(search, zone) is { } stop
                ? $"  Only {found.Count} of {count} passes found. {stop}"
                : $"  Only {found.Count} of {count} passes found in the next {days} days.").ConfigureAwait(false);
        }

        return 0;
    }

    private static string Azimuth(double degrees) => $"{Format.Number(degrees, 1),5}°";

    /// <summary>Finds the satellite's newest element set, checking groups in order and stopping at the first that has it.</summary>
    private static async Task<GpRecord?> FindAsync(CommandContext context, long catalogNumber, bool refresh, CancellationToken token)
    {
        var (env, settings, cache) = context;
        bool anyData = false;
        foreach (string group in settings.Groups)
        {
            GpCacheResult result = await cache.GetGroupAsync(group, token, refresh).ConfigureAwait(false);
            foreach (string warning in result.Warnings)
            {
                await env.Error.WriteLineAsync($"warning: {warning}").ConfigureAwait(false);
            }

            anyData |= result.Records.Count > 0;
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

        await env.Error.WriteLineAsync(anyData
            ? $"NORAD {catalogNumber} is not in the configured CelesTrak groups ({string.Join(", ", settings.Groups)})."
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
}
