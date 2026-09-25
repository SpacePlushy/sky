using System.Globalization;
using Sky.Orbital.Passes;

namespace Sky.Cli;

/// <summary>Output formatting. Everything is culture-invariant, so output reads the same on every machine.</summary>
internal static class Format
{
    /// <summary>An instant as local time in a zone, using the zone's rules for that date.</summary>
    public static string LocalTime(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Clock time only, in a zone.</summary>
    public static string LocalClock(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The nearest whole second, halves rounding up. Pass times are found to 1 ms, so the printed
    /// second is within half a second of the event.
    /// </summary>
    public static DateTimeOffset RoundToSecond(DateTimeOffset instant)
    {
        long remainder = instant.Ticks % TimeSpan.TicksPerSecond;
        return remainder >= TimeSpan.TicksPerSecond / 2
            ? instant.AddTicks(TimeSpan.TicksPerSecond - remainder)
            : instant.AddTicks(-remainder);
    }

    /// <summary>The UTC offset suffix for an instant in a zone, such as -06:00.</summary>
    public static string OffsetSuffix(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        TimeSpan offset = zone.GetUtcOffset(instant);
        return string.Create(CultureInfo.InvariantCulture, $"{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}");
    }

    /// <summary>An elevation setting as entered, without rounding, such as 10 or 30.5.</summary>
    public static string Degrees(double degrees) => degrees.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A bound in degrees, rounded up to 3 decimals so the printed value is still a bound.</summary>
    public static string BoundDegrees(double degrees) =>
        (Math.Ceiling(degrees * 1000.0) / 1000.0).ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>An instant in UTC.</summary>
    public static string Utc(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The zone's UTC offset on a given date, such as UTC-07:00.</summary>
    public static string Offset(DateTimeOffset instant, TimeZoneInfo zone)
    {
        TimeSpan offset = zone.GetUtcOffset(instant);
        return string.Create(CultureInfo.InvariantCulture, $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}");
    }

    /// <summary>A duration as a countdown, such as "in 5 h 22 min".</summary>
    public static string Countdown(TimeSpan span) => span.TotalMinutes < 1
        ? "now"
        : string.Create(CultureInfo.InvariantCulture, $"in {(int)span.TotalHours} h {span.Minutes} min");

    /// <summary>A number with a fixed count of decimals.</summary>
    public static string Number(double value, int decimals) => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>The zone's UTC offset over a window, noting a daylight saving change inside it.</summary>
    public static string OffsetLabel(TimeZoneInfo zone, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(zone);
        string first = Offset(start, zone);
        string last = Offset(end, zone);
        return first == last ? first : $"{first} until the daylight saving change, then {last}";
    }

    /// <summary>Why a pass search stopped early, or null if it covered the whole window.</summary>
    public static string? SearchStop(PassSearchResult result, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.StoppedAt is { } stoppedAt
            ? $"SGP4 stopped at {LocalTime(stoppedAt, zone)} ({result.StoppedBy}); no passes can be predicted after that."
            : null;
    }
}
