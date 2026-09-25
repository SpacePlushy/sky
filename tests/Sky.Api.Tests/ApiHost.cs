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
    private readonly HttpMessageHandler? _online;
    private readonly IReadOnlyList<string> _groups;

    /// <param name="seed">Put the recorded stations response in the cache.</param>
    /// <param name="online">A fake CelesTrak to use in online mode; offline when null.</param>
    /// <param name="groups">The configured groups; stations by default.</param>
    /// <param name="seedFiles">Writes further cache files into the folder.</param>
    /// <param name="start">The clock's start; 2026-09-24 04:00 UTC by default.</param>
    public ApiHost(bool seed = true, HttpMessageHandler? online = null, IReadOnlyList<string>? groups = null, Action<string>? seedFiles = null, DateTimeOffset? start = null)
    {
        Directory.CreateDirectory(_cache);
        if (seed)
        {
            File.Copy(FixturePath, Path.Combine(_cache, "stations.json"));
        }

        seedFiles?.Invoke(_cache);
        _online = online;
        _groups = groups ?? ["stations"];
        Clock = new FakeTimeProvider(start ?? Start);
    }

    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations-2026-09-24.json");

    public FakeTimeProvider Clock { get; }

    public SkySettings Settings() => new(
        "Arizona State Capitol, Phoenix",
        new Geodetic(33.4478, -112.0972, 0.331),
        TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"),
        _groups,
        _cache,
        10.0)
    {
        Offline = _online is null,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SkySettings>();
            services.RemoveAll<SkyClock>();
            services.RemoveAll<GpCache>();
            services.AddSingleton(Settings());
            services.AddSingleton(new SkyClock(Clock));
            services.AddSingleton(sp => new GpCache(_cache, new CelesTrakClient(new HttpClient(_online ?? new NoNetwork())), Clock, offline: _online is null));
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
