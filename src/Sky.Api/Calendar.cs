using System.Globalization;
using System.Text;

namespace Sky.Api;

/// <summary>One event for the calendar export: a visible part of a pass, or a whole pass.</summary>
/// <param name="Start">When it starts.</param>
/// <param name="End">When it ends.</param>
/// <param name="Summary">The event's title.</param>
/// <param name="Description">Details, one item per line.</param>
/// <param name="Key">What identifies the event across exports, such as the satellite and the pass's rise.</param>
internal sealed record CalendarEvent(DateTimeOffset Start, DateTimeOffset End, string Summary, string Description, string Key);

/// <summary>
/// Writes an iCalendar file (RFC 5545) of pass events, each with a display alarm before it starts.
/// Apple Calendar and Outlook keep such alarms; Google Calendar ignores alarms in imported files and
/// applies its own default notifications.
/// </summary>
/// <remarks>
/// Times are UTC, written with a Z (RFC 5545 §3.3.5, form 2), so every calendar shows them in its
/// own zone correctly. Text is escaped (§3.3.11), lines end in CRLF, and lines longer than 75 octets
/// of UTF-8 are folded (§3.1) without splitting a character. Each event's UID comes from its key,
/// which callers make stable across element sets, so importing a later export updates matching
/// events instead of duplicating them. A calendar needs at least one component (§3.6), so an empty
/// list of events is refused.
/// </remarks>
internal static class Calendar
{
    private const int MaximumLineOctets = 75;

    /// <summary>The calendar file for a list of events.</summary>
    /// <param name="name">The calendar's display name.</param>
    /// <param name="events">The events.</param>
    /// <param name="stamp">When the file was made (DTSTAMP).</param>
    /// <param name="alarmMinutes">How many minutes before each event its alarm goes off.</param>
    public static string Write(string name, IReadOnlyCollection<CalendarEvent> events, DateTimeOffset stamp, int alarmMinutes)
    {
        if (events.Count == 0)
        {
            throw new InvalidOperationException("An iCalendar file needs at least one event (RFC 5545 §3.6).");
        }

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Sky Over Phoenix//Pass predictions//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "X-WR-CALNAME:" + Escape(name),
        };

        foreach (CalendarEvent e in events)
        {
            lines.AddRange(
            [
                "BEGIN:VEVENT",
                "UID:" + Escape(e.Key) + "@sky-over-phoenix",
                "DTSTAMP:" + Utc(stamp),
                "DTSTART:" + Utc(e.Start),
                "DTEND:" + Utc(e.End),
                "SUMMARY:" + Escape(e.Summary),
                "DESCRIPTION:" + Escape(e.Description),
                "TRANSP:TRANSPARENT",
                "BEGIN:VALARM",
                "ACTION:DISPLAY",
                "DESCRIPTION:" + Escape(e.Summary),
                string.Create(CultureInfo.InvariantCulture, $"TRIGGER:-PT{alarmMinutes}M"),
                "END:VALARM",
                "END:VEVENT",
            ]);
        }

        lines.Add("END:VCALENDAR");
        var output = new StringBuilder();
        foreach (string line in lines)
        {
            Fold(line, output);
        }

        return output.ToString();
    }

    /// <summary>An instant as an RFC 5545 UTC date-time, rounded to the nearest second.</summary>
    internal static string Utc(DateTimeOffset instant) =>
        RoundToSecond(instant).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The nearest whole second, halves up: the rounding the CLI and the dashboard use too.</summary>
    internal static DateTimeOffset RoundToSecond(DateTimeOffset instant)
    {
        long remainder = instant.UtcTicks % TimeSpan.TicksPerSecond;
        return remainder >= TimeSpan.TicksPerSecond / 2 ? instant.AddTicks(TimeSpan.TicksPerSecond - remainder) : instant.AddTicks(-remainder);
    }

    /// <summary>Escapes TEXT: backslash, semicolon, comma, and line breaks (RFC 5545 §3.3.11).</summary>
    internal static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Appends a content line, folded so no physical line exceeds 75 octets of UTF-8. A continuation
    /// line starts with a space, which counts toward its 75. Characters are never split.
    /// </summary>
    private static void Fold(string line, StringBuilder output)
    {
        int octets = 0;
        int limit = MaximumLineOctets;
        foreach (Rune rune in line.EnumerateRunes())
        {
            int size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                output.Append("\r\n ");
                octets = 1;
            }

            output.Append(rune.ToString());
            octets += size;
        }

        output.Append("\r\n");
    }
}
