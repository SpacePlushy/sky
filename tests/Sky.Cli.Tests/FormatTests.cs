using Sky.Orbital.Passes;
using Sky.Orbital.Propagation;

namespace Sky.Cli.Tests;

public class FormatTests
{
    private static readonly TimeZoneInfo Phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
    private static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");

    [Fact]
    public void Offset_label_is_the_offset_when_it_holds_for_the_whole_window()
    {
        var start = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("UTC-07:00", Format.OffsetLabel(Phoenix, start, start.AddDays(7)));
        Assert.Equal("UTC-07:00", Format.OffsetLabel(Denver, start, start.AddDays(2)));
    }

    [Fact]
    public void Offset_label_says_so_when_daylight_saving_changes_it_within_the_window()
    {
        // Denver moves from UTC-7 to UTC-6 on 2026-03-08; Phoenix does not observe daylight saving.
        var start = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("UTC-07:00 until the daylight saving change, then UTC-06:00", Format.OffsetLabel(Denver, start, start.AddDays(7)));
    }

    [Fact]
    public void A_search_cut_short_by_sgp4_is_explained()
    {
        var stoppedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        string? message = Format.SearchStop(new PassSearchResult([], Sgp4Error.Decayed, stoppedAt), Phoenix);

        Assert.Equal("SGP4 stopped at 2026-09-24 05:00:00 (Decayed); no passes can be predicted after that.", message);
        Assert.Null(Format.SearchStop(new PassSearchResult([], Sgp4Error.None, null), Phoenix));
    }
}
