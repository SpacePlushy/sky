using System.Globalization;
using System.Text.Json;
using Sky.Orbital.Frames;
using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Orbital.Tests.CrossCheck;

/// <summary>
/// Sky against Heavens-Above's published visible passes for the default observer, computed by
/// Heavens-Above from the same element set (Data/HeavensAbove/iss-phoenix-2026-09-25.json).
/// </summary>
/// <remarks>
/// <para>
/// Asserted: every pass Heavens-Above lists is a pass Sky finds, and where Heavens-Above's start or
/// end is a 10° crossing, Sky's rise or set is at most 1.5 s from it. Heavens-Above prints whole
/// seconds, apparently truncated (Sky is 0.3 to 1.1 s later everywhere), so the bound is the 1 s of
/// truncation plus 0.5 s for its own method. Its highest point, at a flat peak, gets the same bound
/// and its printed whole degree.
/// </para>
/// <para>
/// Recorded, not asserted (docs/verification.md): Heavens-Above ends a pass 2.0 to 4.6 s before Sky
/// where the satellite enters shadow, consistent with its counting the fade through the penumbra
/// where Sky uses the Sun's center (assumption A11); and it counts twilight passes as visible
/// earlier than Sky's Sun-below-−6° rule, the definition this project uses.
/// </para>
/// </remarks>
public class HeavensAboveTests
{
    [Fact]
    public void Rise_set_and_peak_match_heavens_above_with_the_same_elements()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "HeavensAbove", "iss-phoenix-2026-09-25.json")));
        var offset = TimeSpan.Parse(json.RootElement.GetProperty("utc_offset").GetString()!.TrimStart('+'), CultureInfo.InvariantCulture);
        var reference = SkyfieldReference.Instance;
        var iss = Sgp4Propagator.Create(reference.MeanElements);
        var phoenix = new TopocentricFrame(reference.ObserverLocation);
        var start = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var passes = PassFinder.Find(iss, phoenix, start, start.AddDays(9), 10.0).Passes;

        int compared = 0;
        foreach (var row in json.RootElement.GetProperty("passes").EnumerateArray())
        {
            DateTimeOffset Local(string name) => new(DateTime.Parse(row.GetProperty(name).GetString()!, CultureInfo.InvariantCulture), offset);
            var haStart = Local("start");
            var pass = passes.Single(p => p.Rise.Time <= haStart.AddSeconds(2) && haStart <= p.Set.Time);

            if (row.GetProperty("start_alt").GetInt32() == 10)
            {
                Assert.InRange((pass.Rise.Time - haStart).TotalSeconds, -1.5, 1.5);
                compared++;
            }

            if (row.GetProperty("end_alt").GetInt32() == 10)
            {
                Assert.InRange((pass.Set.Time - Local("end")).TotalSeconds, -1.5, 1.5);
                compared++;
            }

            // The highest visible point is the culmination when it lies inside the visible part.
            var highest = Local("highest");
            if (Math.Abs((pass.Culmination.Time - highest).TotalSeconds) < 5)
            {
                Assert.InRange((pass.Culmination.Time - highest).TotalSeconds, -1.5, 1.5);
                Assert.Equal(row.GetProperty("highest_alt").GetInt32(), pass.Culmination.ElevationDegrees, 0.5);
                compared++;
            }
        }

        Assert.True(compared >= 15, $"Only {compared} comparable events.");
    }
}
