namespace Sky.Cli;

/// <summary>Everything the CLI touches outside itself, so tests can substitute each part.</summary>
internal sealed record CliEnvironment(
    TimeProvider Time,
    TextWriter Out,
    TextWriter Error,
    string SettingsDirectory,
    string EnvironmentPrefix,
    Func<HttpClient> CreateHttpClient);
