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
    public void A_relative_cache_directory_resolves_against_the_settings_folder()
    {
        // Resolving against the working directory would give each directory its own request history,
        // so two runs from different folders could both request inside CelesTrak's 2-hour window.
        _cli.WriteLocal("""{"CelesTrak":{"CacheDirectory":"cache-relative"}}""");

        var settings = SkySettings.Load(_cli.SettingsDirectory, _cli.EnvironmentPrefix);

        Assert.Equal(Path.Combine(_cli.SettingsDirectory, "cache-relative"), settings.CacheDirectory);
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
}
