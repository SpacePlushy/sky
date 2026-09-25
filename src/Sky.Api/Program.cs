using Microsoft.AspNetCore.Http.HttpResults;
using Sky.Api;
using Sky.CelesTrak;
using Sky.Settings;

// Settings come from the same files and SKY_ environment variables as the CLI.
SkySettings settings;
try
{
    settings = SkySettings.Load(AppContext.BaseDirectory, "SKY_");
}
catch (SettingsException ex)
{
    await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<TimeProvider>(settings.ClockStartUtc is { } start ? new StartedClock(TimeProvider.System, start) : TimeProvider.System);
builder.Services.AddSingleton(sp => new GpCache(
    settings.CacheDirectory,
    new CelesTrakClient(CelesTrakClient.CreateHttpClient()),
    sp.GetRequiredService<TimeProvider>(),
    settings.Offline));
builder.Services.AddSingleton<SatelliteService>();
builder.Services.AddProblemDetails();

var app = builder.Build();
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
