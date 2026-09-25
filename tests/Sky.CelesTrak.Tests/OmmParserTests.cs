using System.Text.Json;
using Sky.CelesTrak;
using Sky.Orbital;
using Sky.Orbital.Elements;
using Sky.Orbital.Propagation;

namespace Sky.CelesTrak.Tests;

public class OmmParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Parses_every_record_in_a_real_celestrak_response()
    {
        var records = OmmParser.Parse(Fixture("stations-2026-09-24.json"));

        Assert.Equal(22, records.Count);
        Assert.Contains(records, r => r.Elements.CatalogNumber == 25544 && r.Name == "ISS (ZARYA)");
        // 6-digit catalog numbers, which TLEs cannot hold.
        Assert.Contains(records, r => r.Elements.CatalogNumber == 100057 && r.Name == "SOYUZ-MS 29");
        Assert.Contains(records, r => r.Elements.CatalogNumber == 100712 && r.Name == "PROGRESS-MS 35");
    }

    [Fact]
    public void Maps_the_iss_record_to_exact_mean_elements()
    {
        var iss = OmmParser.Parse(Fixture("stations-2026-09-24.json")).Single(r => r.Elements.CatalogNumber == 25544);

        // Values copied by hand from the fixture.
        var expected = new MeanElements
        {
            CatalogNumber = 25544,
            Epoch = new DateTimeOffset(2026, 9, 24, 3, 24, 21, TimeSpan.Zero).AddTicks(4_525_440),
            MeanMotion = 15.49258637,
            Eccentricity = 0.00046914,
            Inclination = 51.6318,
            RightAscensionOfAscendingNode = 170.3464,
            ArgumentOfPericenter = 174.6338,
            MeanAnomaly = 185.4701,
            BStar = 0.00018115501,
            MeanMotionDot = 9.634e-05,
            MeanMotionDdot = 0,
        };
        Assert.Equal(expected, iss.Elements);
        Assert.Equal("1998-067A", iss.ObjectId);
        Assert.Equal(0, iss.EphemerisType);
    }

    [Fact]
    public void Reads_the_epoch_as_utc_with_every_microsecond()
    {
        // CelesTrak writes EPOCH without a zone designator; it is UTC by definition.
        var record = OmmParser.Parse(Json(epoch: "2026-09-24T03:24:21.452544")).Single();

        Assert.Equal(TimeSpan.Zero, record.Elements.Epoch.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 3, 24, 21, TimeSpan.Zero).AddTicks(4_525_440), record.Elements.Epoch);
    }

    [Theory]
    [InlineData("2026-09-24T03:24:21", 0L)]
    [InlineData("2026-09-24T03:24:21.5", 5_000_000L)]
    [InlineData("2026-09-24T03:24:21.000001", 10L)]
    public void Accepts_epochs_with_zero_to_six_fractional_digits(string epoch, long extraTicks)
    {
        var record = OmmParser.Parse(Json(epoch: epoch)).Single();

        Assert.Equal(new DateTimeOffset(2026, 9, 24, 3, 24, 21, TimeSpan.Zero).AddTicks(extraTicks), record.Elements.Epoch);
    }

    [Fact]
    public void Matches_the_tle_parser_for_the_same_element_set()
    {
        // Vallado verification case 00005, written as OMM JSON by hand using CelesTrak's documented
        // field mapping: same values and units, epoch as ISO 8601. The TLE path is proven against
        // Vallado's reference output, so identical elements prove the OMM path loses nothing.
        const string omm = """
            [{"OBJECT_NAME":"VANGUARD 1","OBJECT_ID":"1958-002B","EPOCH":"2000-06-27T18:50:19.733568",
              "MEAN_MOTION":10.82419157,"ECCENTRICITY":0.1859667,"INCLINATION":34.2682,"RA_OF_ASC_NODE":348.7242,
              "ARG_OF_PERICENTER":331.7664,"MEAN_ANOMALY":19.3264,"EPHEMERIS_TYPE":0,"CLASSIFICATION_TYPE":"U",
              "NORAD_CAT_ID":5,"ELEMENT_SET_NO":475,"REV_AT_EPOCH":41366,"BSTAR":2.8098e-05,
              "MEAN_MOTION_DOT":2.3e-07,"MEAN_MOTION_DDOT":0}]
            """;
        var fromTle = Tle.Parse(
            "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753",
            "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667");

        var fromOmm = OmmParser.Parse(omm).Single().Elements;

        Assert.Equal(fromTle, fromOmm);
    }

    [Fact]
    public void Real_iss_record_propagates_to_skyfields_positions()
    {
        // End to end from CelesTrak's bytes: Sky's parser and SGP4 against Skyfield's propagation
        // of the same record, at 133 instants over 3 days. Tolerance as in the Vallado verification.
        var iss = OmmParser.Parse(Fixture("stations-2026-09-24.json")).Single(r => r.Elements.CatalogNumber == 25544);
        var propagator = Sgp4Propagator.Create(iss.ToSgp4Elements());
        using var reference = JsonDocument.Parse(Fixture("iss-phoenix-2026-09-24.json"));

        int compared = 0;
        foreach (var state in reference.RootElement.GetProperty("states").EnumerateArray())
        {
            var utc = state.GetProperty("utc").GetDateTimeOffset();
            var expected = state.GetProperty("teme_km");
            var actual = propagator.Propagate(utc).State.Position;
            var difference = new Vec3(
                actual.X - expected[0].GetDouble(), actual.Y - expected[1].GetDouble(), actual.Z - expected[2].GetDouble());

            Assert.True(difference.Length <= 2e-7, $"{utc:O}: {difference.Length:E2} km from Skyfield.");
            compared++;
        }

        Assert.Equal(133, compared);
    }

    [Fact]
    public void Refuses_sgp4_xp_element_sets()
    {
        // Ephemeris type 4 is SGP4-XP, a different theory. Propagating those elements with SGP4
        // would give wrong positions without any error, so the conversion must refuse.
        var record = OmmParser.Parse(Json(ephemerisType: 4)).Single();

        Assert.Equal(4, record.EphemerisType);
        Assert.Throws<InvalidOperationException>(() => record.ToSgp4Elements());
    }

    [Fact]
    public void Tolerates_a_missing_or_blank_name()
    {
        var records = OmmParser.Parse($"[{Record(name: null)},{Record(name: "")}]");

        Assert.All(records, r => Assert.True(string.IsNullOrEmpty(r.Name)));
    }

    [Theory]
    [InlineData("GP data has not updated since your last successful download of GROUP=stations. Data is updated once every 2 hours.")]
    [InlineData("No GP data found")]
    [InlineData("")]
    [InlineData("{\"error\":\"not an array\"}")]
    [InlineData("[{\"NORAD_CAT_ID\":25544")]
    public void Rejects_a_body_that_is_not_a_json_array_of_records(string body)
    {
        var error = Assert.Throws<FormatException>(() => OmmParser.Parse(body));

        Assert.Contains("CelesTrak", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EPOCH")]
    [InlineData("MEAN_MOTION")]
    [InlineData("ECCENTRICITY")]
    [InlineData("INCLINATION")]
    [InlineData("RA_OF_ASC_NODE")]
    [InlineData("ARG_OF_PERICENTER")]
    [InlineData("MEAN_ANOMALY")]
    [InlineData("BSTAR")]
    [InlineData("NORAD_CAT_ID")]
    public void Rejects_a_record_missing_a_required_field(string field)
    {
        var record = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Record())!;
        record.Remove(field);

        var error = Assert.Throws<FormatException>(() => OmmParser.Parse($"[{JsonSerializer.Serialize(record)}]"));

        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-13-01T00:00:00")]
    [InlineData("2026-09-24 03:24:21")]
    [InlineData("24 Sep 2026")]
    public void Rejects_a_malformed_epoch(string epoch)
    {
        Assert.Throws<FormatException>(() => OmmParser.Parse(Json(epoch: epoch)));
    }

    private static string Json(string epoch = "2026-09-24T03:24:21.452544", int ephemerisType = 0) =>
        $"[{Record(epoch: epoch, ephemerisType: ephemerisType)}]";

    private static string Record(string? name = "ISS (ZARYA)", string epoch = "2026-09-24T03:24:21.452544", int ephemerisType = 0)
    {
        string nameJson = name is null ? "null" : $"\"{name}\"";
        return $$"""
            {"OBJECT_NAME":{{nameJson}},"OBJECT_ID":"1998-067A","EPOCH":"{{epoch}}","MEAN_MOTION":15.49258637,
             "ECCENTRICITY":0.00046914,"INCLINATION":51.6318,"RA_OF_ASC_NODE":170.3464,"ARG_OF_PERICENTER":174.6338,
             "MEAN_ANOMALY":185.4701,"EPHEMERIS_TYPE":{{ephemerisType}},"CLASSIFICATION_TYPE":"U","NORAD_CAT_ID":25544,
             "ELEMENT_SET_NO":999,"REV_AT_EPOCH":58709,"BSTAR":0.00018115501,"MEAN_MOTION_DOT":9.634e-05,"MEAN_MOTION_DDOT":0}
            """;
    }
}
