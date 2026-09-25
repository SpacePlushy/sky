using Sky.Settings;
namespace Sky.Cli.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly CliHarness _cli = new(new DateTimeOffset(2026, 9, 24, 4, 0, 0, TimeSpan.Zero));

    public void Dispose() => _cli.Dispose();

    [Fact]
    public void Committed_defaults_are_the_public_capitol_location_in_phoenix_time()
    {
        var settings = SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix);

        Assert.Equal("Arizona State Capitol, Phoenix", settings.ObserverName);
        Assert.Equal(33.4478, settings.Observer.LatitudeDegrees);
        Assert.Equal(-112.0972, settings.Observer.LongitudeDegrees);
        Assert.Equal(0.331, settings.Observer.HeightKm, 1e-12);
        Assert.Equal("America/Phoenix", settings.TimeZone.Id);
        Assert.Equal(10.0, settings.MinimumElevationDegrees);
    }

    [Fact]
    public void Local_file_overrides_the_committed_observer()
    {
        _cli.WriteLocal("""{"Observer":{"Name":"Home","LatitudeDegrees":33.5,"LongitudeDegrees":-112.0,"HeightMeters":340}}""");

        var settings = SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix);

        Assert.Equal("Home", settings.ObserverName);
        Assert.Equal(33.5, settings.Observer.LatitudeDegrees);
        Assert.Equal(-112.0, settings.Observer.LongitudeDegrees);
        Assert.Equal(0.340, settings.Observer.HeightKm, 1e-12);
        Assert.Equal("America/Phoenix", settings.TimeZone.Id); // untouched keys keep their defaults
    }

    [Fact]
    public void Environment_variables_override_both_files()
    {
        _cli.WriteLocal("""{"Observer":{"LatitudeDegrees":33.5}}""");
        _cli.SetEnvironment("Observer__LatitudeDegrees", "34.25");

        var settings = SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix);

        Assert.Equal(34.25, settings.Observer.LatitudeDegrees);
    }

    [Theory]
    [InlineData("""{"Observer":{"LatitudeDegrees":91}}""", "Observer:LatitudeDegrees")]
    [InlineData("""{"Observer":{"LongitudeDegrees":-181}}""", "Observer:LongitudeDegrees")]
    [InlineData("""{"Observer":{"HeightMeters":12000}}""", "Observer:HeightMeters")]
    [InlineData("""{"Observer":{"TimeZone":"-07:00"}}""", "Observer:TimeZone")]
    [InlineData("""{"Observer":{"TimeZone":"Not/AZone"}}""", "Observer:TimeZone")]
    [InlineData("""{"Observer":{"TimeZone":"US Mountain Standard Time"}}""", "Observer:TimeZone")] // a Windows ID, not IANA
    [InlineData("""{"Observer":{"Latitude":33.5}}""", "Observer:Latitude")] // misspelled: not a setting
    [InlineData("""{"Passes":{"MinimumElevation":20}}""", "Passes:MinimumElevation")]
    [InlineData("""{"Passes":{"MinimumElevationDegrees":90}}""", "Passes:MinimumElevationDegrees")]
    [InlineData("""{"CelesTrak":{"Groups":""}}""", "CelesTrak:Groups")]
    [InlineData("""{"CelesTrak":{"Groups":"stations,../x"}}""", "CelesTrak:Groups")]
    public void Rejects_invalid_settings_and_names_the_setting(string local, string setting)
    {
        _cli.WriteLocal(local);

        var error = Assert.Throws<SettingsException>(() => SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix));

        Assert.Contains(setting, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_keys_from_environment_variables_are_rejected()
    {
        // The approved plan's example, SKY_Observer__Latitude, names a key that does not exist.
        _cli.SetEnvironment("Observer__Latitude", "34.25");

        var error = Assert.Throws<SettingsException>(() => SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix));

        Assert.Contains("Observer:Latitude is not a setting", error.Message, StringComparison.Ordinal);
        Assert.Contains("LatitudeDegrees", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_cache_directory_is_the_same_folder_for_every_program()
    {
        // The CLI and the API read the same settings from their own folders. Resolving a relative
        // path against either folder, or the working directory, would give each its own request
        // history, so both could request inside CelesTrak's 2-hour window. It resolves against the
        // per-user sky folder instead.
        using var other = new CliHarness(new DateTimeOffset(2026, 9, 24, 4, 0, 0, TimeSpan.Zero));
        _cli.WriteLocal("""{"CelesTrak":{"CacheDirectory":"cache-relative"}}""");
        other.WriteLocal("""{"CelesTrak":{"CacheDirectory":"cache-relative"}}""");

        var first = SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix);
        var second = SkySettings.Load(other.SettingsDirectory, other.EnvironmentPrefix);

        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sky", "cache-relative");
        Assert.Equal(expected, first.CacheDirectory);
        Assert.Equal(expected, second.CacheDirectory);
    }

    [Theory]
    [InlineData("")]           // no per-user data folder at all
    [InlineData("relative")]   // one that is not an absolute path
    public void With_no_per_user_folder_an_absolute_cache_directory_still_works(string localApplicationData)
    {
        // A container's app user can have no home folder. An absolute CacheDirectory must not need
        // one; this crashed the Docker image once. A relative path or the default does, and says so.
        string absolute = Path.Combine(Path.GetTempPath(), "sky-absolute-cache");
        _cli.WriteLocal(System.Text.Json.JsonSerializer.Serialize(new { CelesTrak = new { CacheDirectory = absolute } }));
        Assert.Equal(absolute, SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix, localApplicationData).CacheDirectory);

        _cli.WriteLocal("""{"CelesTrak":{"CacheDirectory":"cache-relative"}}""");
        var relative = Assert.Throws<SettingsException>(() => SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix, localApplicationData));
        Assert.Contains("CelesTrak:CacheDirectory must be an absolute path", relative.Message, StringComparison.Ordinal);

        _cli.WriteLocal("{}");
        Assert.Throws<SettingsException>(() => SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix, localApplicationData));
    }

    [Fact]
    public void Phoenix_times_use_the_zone_rules_not_a_fixed_offset()
    {
        // Arizona does not observe daylight saving time, so Phoenix is UTC-7 all year. Denver, in the
        // same standard zone, switches to UTC-6 in summer. Formatting through TimeZoneInfo gets both
        // right, which a hardcoded offset could not.
        var phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        var january = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var july = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-01-15 05:00:00", Format.LocalTime(january, phoenix));
        Assert.Equal("2026-07-15 05:00:00", Format.LocalTime(july, phoenix));
        Assert.Equal("2026-01-15 05:00:00", Format.LocalTime(january, denver));
        Assert.Equal("2026-07-15 06:00:00", Format.LocalTime(july, denver));
    }

    [Fact]
    public void Offline_satellites_and_clock_start_are_read_and_default_sensibly()
    {
        var defaults = SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix);
        Assert.False(defaults.Offline);
        Assert.Equal([25544L], defaults.Satellites);
        Assert.Null(defaults.ClockStartUtc);

        _cli.WriteLocal("""{"CelesTrak":{"Offline":true},"Dashboard":{"Satellites":"25544, 48274,25544"},"Clock":{"StartUtc":"2026-09-24T04:00:00Z"}}""");
        var settings = SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix);

        Assert.True(settings.Offline);
        Assert.Equal([25544L, 48274L], settings.Satellites);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 4, 0, 0, TimeSpan.Zero), settings.ClockStartUtc);
    }

    [Theory]
    [InlineData("""{"CelesTrak":{"Offline":"yes"}}""", "CelesTrak:Offline")]
    [InlineData("""{"Dashboard":{"Satellites":"25544,ISS"}}""", "Dashboard:Satellites")]
    [InlineData("""{"Dashboard":{"Satellites":"-5"}}""", "Dashboard:Satellites")]
    [InlineData("""{"CelesTrak":{"Offline":true},"Clock":{"StartUtc":"2026-09-24T04:00:00"}}""", "Clock:StartUtc")]
    [InlineData("""{"CelesTrak":{"Offline":true},"Clock":{"StartUtc":"2026-09-24T04:00:00-07:00"}}""", "Clock:StartUtc")]
    [InlineData("""{"Clock":{"Start":"2026-09-24T04:00:00Z"}}""", "Clock:Start")]
    [InlineData("""{"Clock":{"StartUtc":"2026-09-24T04:00:00Z"}}""", "Clock:StartUtc needs CelesTrak:Offline")] // a simulated clock must never reach the request history
    public void Rejects_invalid_new_settings_and_names_them(string local, string setting)
    {
        _cli.WriteLocal(local);

        var error = Assert.Throws<SettingsException>(() => SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix));
        Assert.Contains(setting, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_started_clock_shows_the_start_instant_and_runs_at_real_speed()
    {
        var real = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var start = new DateTimeOffset(2026, 9, 24, 4, 0, 0, TimeSpan.Zero);
        var clock = new StartedClock(real, start);

        Assert.Equal(start, clock.GetUtcNow());
        real.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(start.AddSeconds(90), clock.GetUtcNow());
    }
}
