using System.Globalization;
using System.Text.Json;
using Sky.Orbital.Elements;

namespace Sky.CelesTrak;

/// <summary>Reads CelesTrak GP data in OMM JSON format (gp.php with FORMAT=JSON).</summary>
/// <remarks>
/// Field names and units follow https://celestrak.org/NORAD/documentation/gp-data-formats.php.
/// Angles are degrees, mean motion is revolutions per day, and EPOCH is ISO 8601 in UTC
/// without a zone designator. Numbers may appear as integers or decimals, so every orbital
/// field is read as a double. Anything that is not a JSON array of element sets, such as the
/// plain-text notice CelesTrak sends with a 403, is rejected with the start of the body quoted.
/// </remarks>
public static class OmmParser
{
    private static readonly string[] EpochFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    /// <summary>Parses a CelesTrak JSON response into element sets.</summary>
    /// <exception cref="FormatException">The response is not a JSON array of valid element sets.</exception>
    public static IReadOnlyList<GpRecord> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"CelesTrak response is not JSON: \"{Excerpt(json)}\"", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"CelesTrak response is not a JSON array of element sets: \"{Excerpt(json)}\"");
            }

            var records = new List<GpRecord>(document.RootElement.GetArrayLength());
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                records.Add(ParseRecord(element));
            }

            return records;
        }
    }

    private static GpRecord ParseRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("CelesTrak element set is not a JSON object.");
        }

        long catalogNumber = RequiredLong(record, "NORAD_CAT_ID");
        string context = $"CelesTrak element set for NORAD {catalogNumber}";

        return new GpRecord
        {
            Name = OptionalString(record, "OBJECT_NAME"),
            ObjectId = OptionalString(record, "OBJECT_ID"),
            EphemerisType = (int)OptionalDouble(record, "EPHEMERIS_TYPE", context),
            Elements = new MeanElements
            {
                CatalogNumber = catalogNumber,
                Epoch = ParseEpoch(RequiredString(record, "EPOCH", context), context),
                MeanMotion = RequiredDouble(record, "MEAN_MOTION", context),
                Eccentricity = RequiredDouble(record, "ECCENTRICITY", context),
                Inclination = RequiredDouble(record, "INCLINATION", context),
                RightAscensionOfAscendingNode = RequiredDouble(record, "RA_OF_ASC_NODE", context),
                ArgumentOfPericenter = RequiredDouble(record, "ARG_OF_PERICENTER", context),
                MeanAnomaly = RequiredDouble(record, "MEAN_ANOMALY", context),
                BStar = RequiredDouble(record, "BSTAR", context),
                MeanMotionDot = OptionalDouble(record, "MEAN_MOTION_DOT", context),
                MeanMotionDdot = OptionalDouble(record, "MEAN_MOTION_DDOT", context),
            },
        };
    }

    private static DateTimeOffset ParseEpoch(string text, string context)
    {
        // UTC by definition; parse it as UTC regardless of the machine's time zone.
        if (!DateTime.TryParseExact(
                text,
                EpochFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime utc))
        {
            throw new FormatException($"{context} has an EPOCH that is not ISO 8601 UTC: \"{text}\".");
        }

        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static string RequiredString(JsonElement record, string name, string context) =>
        record.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"{context} is missing {name}.");

    private static string? OptionalString(JsonElement record, string name) =>
        record.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double RequiredDouble(JsonElement record, string name, string context) =>
        record.TryGetProperty(name, out JsonElement value)
            ? ReadDouble(value, name, context)
            : throw new FormatException($"{context} is missing {name}.");

    private static double OptionalDouble(JsonElement record, string name, string context) =>
        record.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? ReadDouble(value, name, context)
            : 0.0;

    private static double ReadDouble(JsonElement value, string name, string context)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        throw new FormatException($"{context} has a {name} that is not a finite number: {value.GetRawText()}.");
    }

    private static long RequiredLong(JsonElement record, string name)
    {
        if (!record.TryGetProperty(name, out JsonElement value))
        {
            throw new FormatException($"CelesTrak element set is missing {name}.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        throw new FormatException($"CelesTrak element set has a {name} that is not a whole number: {value.GetRawText()}.");
    }

    private static string Excerpt(string text)
    {
        string oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 200 ? oneLine : string.Concat(oneLine.AsSpan(0, 200), "...");
    }
}
