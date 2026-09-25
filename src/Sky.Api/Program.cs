using Sky.Api;
using Sky.CelesTrak;
using Sky.Settings;

var builder = WebApplication.CreateBuilder(args);

// Only loopback Host headers, unless an operator says otherwise: a page on another site that
// rebinds its DNS name to 127.0.0.1 would otherwise be same-origin with the API and could read
// /api/config, which holds the observer's location.
if (string.IsNullOrWhiteSpace(builder.Configuration["AllowedHosts"]))
{
    builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
}

// The dashboard polls every second; per-request information logging would grow logs without end.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// Settings come from the same files and SKY_ environment variables as the CLI. They are loaded
// when first needed, so a test host that supplies its own never reads the owner's local file. The
// dashboard's clock is its own type, so the framework keeps the system clock and building the host
// never loads settings; invalid settings are reported by the check after Build.
builder.Services.AddSingleton(_ => SkySettings.Load(AppContext.BaseDirectory, "SKY_"));
builder.Services.AddSingleton(sp => new SkyClock(sp.GetRequiredService<SkySettings>().ClockStartUtc is { } start
    ? new StartedClock(TimeProvider.System, start)
    : TimeProvider.System));
builder.Services.AddSingleton(sp =>
{
    SkySettings s = sp.GetRequiredService<SkySettings>();
    return new GpCache(s.CacheDirectory, new CelesTrakClient(CelesTrakClient.CreateHttpClient()), sp.GetRequiredService<SkyClock>().Time, s.Offline);
});
builder.Services.AddSingleton<SatelliteService>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Fail at startup, with the message, if the settings are invalid.
try
{
    _ = app.Services.GetRequiredService<SkySettings>();
}
catch (SettingsException ex)
{
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 1;
}
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    try
    {
        return await next(context).ConfigureAwait(false);
    }
    catch (ApiException ex)
    {
        return TypedResults.Problem(statusCode: ex.Status, title: ex.Title, detail: ex.Message);
    }
});

api.MapGet("/config", (SatelliteService service) =>
{
    SkySettings s = service.Settings;
    return TypedResults.Ok(new ConfigResponse(
        new ObserverDto(s.ObserverName, s.Observer.LatitudeDegrees, s.Observer.LongitudeDegrees, s.Observer.HeightKm * 1000.0, s.TimeZone.Id),
        s.MinimumElevationDegrees,
        s.Satellites,
        s.Offline,
        s.ClockStartUtc is not null,
        s.ClockStartUtc is { } start ? SatelliteService.Utc(start) : null,
        SatelliteService.Utc(service.Now)));
});

api.MapGet("/health", async (SatelliteService service, CancellationToken token) =>
    TypedResults.Ok(await service.HealthAsync(token).ConfigureAwait(false)));

api.MapGet("/satellites", async (SatelliteService service, CancellationToken token) =>
    TypedResults.Ok(await service.SatellitesAsync(token).ConfigureAwait(false)));

api.MapGet("/satellites/{id:long}/now", async (long id, DateTimeOffset? at, SatelliteService service, CancellationToken token) =>
    TypedResults.Ok(await service.NowAsync(id, at, token).ConfigureAwait(false)));

api.MapGet("/satellites/{id:long}/track", async (long id, double? minutes, SatelliteService service, CancellationToken token) =>
    TypedResults.Ok(await service.TrackAsync(id, minutes, token).ConfigureAwait(false)));

api.MapGet("/satellites/{id:long}/passes", async (long id, int? days, double? minElevation, SatelliteService service, CancellationToken token) =>
    TypedResults.Ok(await service.PassesAsync(id, days, minElevation, token).ConfigureAwait(false)));

api.MapGet("/satellites/{id:long}/passes.ics", async (long id, int? days, bool? visibleOnly, int? alarm, SatelliteService service, CancellationToken token) =>
{
    string calendar = await service.CalendarAsync(id, days, visibleOnly, alarm, token).ConfigureAwait(false);
    return TypedResults.File(System.Text.Encoding.UTF8.GetBytes(calendar), "text/calendar; charset=utf-8", $"sky-{id}-passes.ics");
});

// Anything else under /api is a 404, never the dashboard's page.
api.MapFallback(() => TypedResults.Problem(statusCode: 404, title: "Not found"));

// The built dashboard, when present, handles every other route.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
{
    app.MapFallbackToFile("index.html");
}

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>The API's entry point, visible to the test host.</summary>
public partial class Program;
