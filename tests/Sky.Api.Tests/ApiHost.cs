using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Sky.CelesTrak;
using Sky.Orbital.Frames;
using Sky.Settings;

namespace Sky.Api.Tests;

/// <summary>
/// The real API in memory, offline, on a fake clock, with the recorded CelesTrak response seeded
/// into a temporary cache. Nothing it does can reach the network.
/// </summary>
internal sealed class ApiHost : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Start = new(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "sky-api-tests", Guid.NewGuid().ToString("N"));

    public ApiHost(bool seed = true)
    {
        Directory.CreateDirectory(_cache);
        if (seed)
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations-2026-09-24.json"), Path.Combine(_cache, "stations.json"));
        }
    }

    public FakeTimeProvider Clock { get; } = new(Start);

    public static SkySettings Settings(string cache) => new(
        "Arizona State Capitol, Phoenix",
        new Geodetic(33.4478, -112.0972, 0.331),
        TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"),
        ["stations"],
        cache,
        10.0)
    {
        Offline = true,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SkySettings>();
            services.RemoveAll<TimeProvider>();
            services.RemoveAll<GpCache>();
            services.AddSingleton(Settings(_cache));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(sp => new GpCache(_cache, new CelesTrakClient(new HttpClient(new NoNetwork())), Clock, offline: true));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    /// <summary>Fails any request, as a second guarantee on top of offline mode.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"The API tried to reach {request.RequestUri} during a test.");
    }
}
