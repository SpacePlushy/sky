using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Sky.Orbital.Frames;

namespace Sky.Settings;

/// <summary>Validated settings, shared by the CLI and the dashboard API.</summary>
/// <remarks>
/// Layers, later ones winning: appsettings.json (committed, public defaults), then
/// appsettings.Local.json (gitignored, your real location), then environment variables with the
/// SKY_ prefix, such as SKY_Observer__LatitudeDegrees. Observer height is treated as height above
/// the WGS-84 ellipsoid (assumption A7): published elevations are above sea level, about 30 m
/// different in Phoenix, which changes look angles by under 0.004 degrees.
/// </remarks>
/// <param name="ObserverName">A label for the observer.</param>
/// <param name="Observer">The observer's position on the WGS-84 ellipsoid.</param>
/// <param name="TimeZone">The observer's IANA time zone, used only to display times.</param>
/// <param name="Groups">CelesTrak groups to search, in order.</param>
/// <param name="CacheDirectory">Where GP data and the request history live.</param>
/// <param name="MinimumElevationDegrees">The elevation a pass must reach.</param>
public sealed partial record SkySettings(
    string ObserverName,
    Geodetic Observer,
    TimeZoneInfo TimeZone,
    IReadOnlyList<string> Groups,
    string CacheDirectory,
    double MinimumElevationDegrees)
{
    /// <summary>When true, serve cached GP data only and never contact CelesTrak.</summary>
    public bool Offline { get; init; }

    /// <summary>Satellites the dashboard offers, by NORAD catalog number; the first is the default.</summary>
    public IReadOnlyList<long> Satellites { get; init; } = [25544];

    /// <summary>
    /// If set, the clock starts at this instant when the program starts and runs at real speed
    /// from there. For demonstrations and tests with recorded data; unset in normal use.
    /// </summary>
    public DateTimeOffset? ClockStartUtc { get; init; }

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
        foreach (var (section, known) in KnownKeys)
        {
            foreach (IConfigurationSection child in config.GetSection(section).GetChildren())
            {
                if (!known.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
                {
                    // A misspelled key would otherwise be ignored silently and the default used.
                    problems.Add($"{section}:{child.Key} is not a setting. {section} settings are: {string.Join(", ", known)}.");
                }
            }
        }

        string name = string.IsNullOrWhiteSpace(config["Observer:Name"]) ? "Observer" : config["Observer:Name"]!;
        double latitude = Number(config, "Observer:LatitudeDegrees", -90, 90, problems);
        double longitude = Number(config, "Observer:LongitudeDegrees", -180, 180, problems);
        double heightMeters = Number(config, "Observer:HeightMeters", -500, 9000, problems);
        double minimumElevation = Number(config, "Passes:MinimumElevationDegrees", 0, 89, problems);
        TimeZoneInfo? zone = IanaTimeZone(config["Observer:TimeZone"], problems);
        IReadOnlyList<string> groups = ParseGroups(config["CelesTrak:Groups"], problems);
        bool offline = Flag(config, "CelesTrak:Offline", problems);
        IReadOnlyList<long> satellites = ParseSatellites(config["Dashboard:Satellites"], problems);
        DateTimeOffset? clockStart = ParseInstant(config["Clock:StartUtc"], "Clock:StartUtc", problems);

        if (problems.Count > 0)
        {
            throw new SettingsException("Invalid settings:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  " + p)));
        }

        // A relative directory resolves against the settings folder, never the working directory,
        // so every run shares one request history and one 2-hour rule.
        string cacheDirectory = string.IsNullOrWhiteSpace(config["CelesTrak:CacheDirectory"])
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sky", "celestrak")
            : Path.GetFullPath(config["CelesTrak:CacheDirectory"]!, settingsDirectory);

        return new SkySettings(name, new Geodetic(latitude, longitude, heightMeters / 1000.0), zone!, groups, cacheDirectory, minimumElevation)
        {
            Offline = offline,
            Satellites = satellites,
            ClockStartUtc = clockStart,
        };
    }

    private static readonly (string Section, string[] Keys)[] KnownKeys =
    [
        ("Observer", ["Name", "LatitudeDegrees", "LongitudeDegrees", "HeightMeters", "TimeZone"]),
        ("CelesTrak", ["Groups", "CacheDirectory", "Offline"]),
        ("Passes", ["MinimumElevationDegrees"]),
        ("Dashboard", ["Satellites"]),
        ("Clock", ["StartUtc"]),
    ];

    private static bool Flag(IConfiguration config, string key, List<string> problems)
    {
        string? text = config[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (bool.TryParse(text, out bool value))
        {
            return value;
        }

        problems.Add($"{key} must be true or false; got \"{text}\".");
        return false;
    }

    private static List<long> ParseSatellites(string? text, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [25544];
        }

        var numbers = new List<long>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number is > 0 and < 1_000_000_000)
            {
                numbers.Add(number);
            }
            else
            {
                problems.Add($"Dashboard:Satellites must be a comma-separated list of NORAD catalog numbers such as \"25544,48274\"; got \"{text}\".");
                return [25544];
            }
        }

        return numbers.Count > 0 ? numbers.Distinct().ToList() : [25544];
    }

    private static DateTimeOffset? ParseInstant(string? text, string key, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // UTC only, written with a Z, so a local-time misreading cannot shift the clock.
        if (text.EndsWith('Z')
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset instant))
        {
            return instant;
        }

        problems.Add($"{key} must be a UTC instant ending in Z, such as 2026-09-24T04:00:00Z; got \"{text}\".");
        return null;
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
public sealed class SettingsException(string message) : Exception(message);
