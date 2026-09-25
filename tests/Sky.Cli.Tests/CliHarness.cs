using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Sky.Cli.Tests;

/// <summary>
/// Runs the real CLI in a temporary settings directory, with a fake clock, captured output, and a
/// fake CelesTrak that serves the recorded stations response. No test touches the network or the
/// real environment variables.
/// </summary>
internal sealed class CliHarness : IDisposable
{
    public CliHarness(DateTimeOffset now)
    {
        Directory.CreateDirectory(SettingsDirectory);
        Clock = new FakeTimeProvider(now);
        File.WriteAllText(Path.Combine(SettingsDirectory, "appsettings.json"), File.ReadAllText(CommittedSettings));
        // Point the cache at a temporary directory and fetch only the recorded group.
        WriteLocal(System.Text.Json.JsonSerializer.Serialize(new { CelesTrak = new { Groups = "stations", CacheDirectory } }));
    }

    public static string CommittedSettings => Path.Combine(AppContext.BaseDirectory, "Committed", "appsettings.json");

    public string SettingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "sky-cli-tests", Guid.NewGuid().ToString("N"));

    public string CacheDirectory => Path.Combine(SettingsDirectory, "cache");

    public string EnvironmentPrefix { get; } = $"SKYTEST_{Guid.NewGuid():N}_";

    public FakeTimeProvider Clock { get; }

    public List<Uri> Requests { get; } = [];

    public bool CelesTrakReachable { get; set; } = true;

    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    public void WriteLocal(string json) => File.WriteAllText(Path.Combine(SettingsDirectory, "appsettings.Local.json"), json);

    public void SetEnvironment(string name, string value) => Environment.SetEnvironmentVariable(EnvironmentPrefix + name, value);

    public Task<int> RunAsync(params string[] args) => SkyCli.RunAsync(
        args,
        new CliEnvironment(Clock, Out, Error, SettingsDirectory, EnvironmentPrefix, () => new HttpClient(new FakeCelesTrak(this))),
        TestContext.Current.CancellationToken);

    public void Dispose()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(n => n.StartsWith(EnvironmentPrefix, StringComparison.Ordinal)))
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        if (Directory.Exists(SettingsDirectory))
        {
            Directory.Delete(SettingsDirectory, recursive: true);
        }
    }

    private sealed class FakeCelesTrak(CliHarness harness) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            harness.Requests.Add(request.RequestUri!);
            if (!harness.CelesTrakReachable)
            {
                throw new HttpRequestException("Simulated network failure.");
            }

            string body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations-2026-09-24.json"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
