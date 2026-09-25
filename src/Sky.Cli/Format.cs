using System.Globalization;

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
}
