using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Sky.Orbital.Frames;

namespace Sky.Cli;

/// <summary>Validated settings for the CLI.</summary>
/// <remarks>
/// Layers, later ones winning: appsettings.json (committed, public defaults), then
/// appsettings.Local.json (gitignored, your real location), then environment variables with the
/// SKY_ prefix, such as SKY_Observer__LatitudeDegrees. Observer height is treated as height above
/// the WGS-84 ellipsoid (assumption A7): published elevations are above sea level, about 30 m
/// different in Phoenix, which changes look angles by under 0.004 degrees.
/// </remarks>
internal sealed partial record SkySettings(
    string ObserverName,
    Geodetic Observer,
    TimeZoneInfo TimeZone,
    IReadOnlyList<string> Groups,
    string CacheDirectory,
    double MinimumElevationDegrees)
{
    /// <summary>Loads and validates settings, reporting every problem at once.</summary>
    /// <exception cref="SettingsException">A setting is missing or invalid.</exception>
    public static SkySettings Load(string settingsDirectory, string environmentPrefix)
    {
        IConfiguration config;
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(settingsDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Local.json", optional: true)
                .AddEnvironmentVariables(environmentPrefix)
                .Build();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException)
        {
            throw new SettingsException($"Could not read settings from {settingsDirectory}: {ex.Message}");
        }

        var problems = new List<string>();
        string name = string.IsNullOrWhiteSpace(config["Observer:Name"]) ? "Observer" : config["Observer:Name"]!;
        double latitude = Number(config, "Observer:LatitudeDegrees", -90, 90, problems);
        double longitude = Number(config, "Observer:LongitudeDegrees", -180, 180, problems);
        double heightMeters = Number(config, "Observer:HeightMeters", -500, 9000, problems);
        double minimumElevation = Number(config, "Passes:MinimumElevationDegrees", 0, 89, problems);
        TimeZoneInfo? zone = IanaTimeZone(config["Observer:TimeZone"], problems);
        IReadOnlyList<string> groups = ParseGroups(config["CelesTrak:Groups"], problems);

        if (problems.Count > 0)
        {
            throw new SettingsException("Invalid settings:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  " + p)));
        }

        string cacheDirectory = string.IsNullOrWhiteSpace(config["CelesTrak:CacheDirectory"])
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sky", "celestrak")
            : config["CelesTrak:CacheDirectory"]!;

        return new SkySettings(name, new Geodetic(latitude, longitude, heightMeters / 1000.0), zone!, groups, cacheDirectory, minimumElevation);
    }

    private static double Number(IConfiguration config, string key, double minimum, double maximum, List<string> problems)
    {
        string? text = config[key];
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && value >= minimum && value <= maximum)
        {
            return value;
        }

        problems.Add(FormattableString.Invariant($"{key} must be a number from {minimum} to {maximum}; got \"{text}\"."));
        return double.NaN;
    }

    private static TimeZoneInfo? IanaTimeZone(string? id, List<string> problems)
    {
        // IANA names only (America/Phoenix), never fixed offsets: the zone's rules decide the offset
        // for each date, which is what makes daylight saving time and Arizona's lack of it both work.
        if (!string.IsNullOrWhiteSpace(id)
            && TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone)
            && zone.HasIanaId)
        {
            return zone;
        }

        problems.Add($"Observer:TimeZone must be an IANA time zone name such as America/Phoenix; got \"{id}\".");
        return null;
    }

    private static List<string> ParseGroups(string? text, List<string> problems)
    {
        List<string> groups = (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (groups.Count == 0 || !groups.All(g => GroupName().IsMatch(g)))
        {
            problems.Add($"CelesTrak:Groups must be a comma-separated list of CelesTrak group names such as \"stations,visual\"; got \"{text}\".");
        }

        return groups;
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex GroupName();
}

/// <summary>Settings that are missing or invalid.</summary>
internal sealed class SettingsException(string message) : Exception(message);
