using Xunit;

public sealed class UiTextTests
{
    [Theory]
    [InlineData(3, 2, 4)]
    [InlineData(3, 4, 2)]
    public void SkipFavoritesDivider_PreservesCursorDirection(int divider, int previousItem, int expectedItem)
    {
        Assert.Equal(expectedItem, UiText.SkipFavoritesDivider(divider, previousItem, profileCount: 6, favoriteCount: 3));
    }

    [Fact]
    public void ProfileListItem_AddsDividerOffsetOnlyForNormalProfiles()
    {
        Assert.Equal(2, UiText.ProfileListItem(2, profileCount: 6, favoriteCount: 3));
        Assert.Equal(4, UiText.ProfileListItem(3, profileCount: 6, favoriteCount: 3));
    }

    [Theory]
    [InlineData(true, true, "💥")]
    [InlineData(true, false, "💥")]
    [InlineData(false, true, "●")]
    [InlineData(false, false, "○")]
    public void ProfileGlyph_UsesOperationalStateWithBrokenTakingPriority(bool isBroken, bool isRunning, string expected)
    {
        Assert.Equal(expected, UiText.ProfileGlyph(isBroken, isRunning));
    }

    [Fact]
    public void ProfileRows_PreservesMarkerVisionSuffixAndBothEndsOfNameWithoutTags()
    {
        var row = Assert.Single(UiText.ProfileRows([new UiText.ProfileRowData("○", true, "diffusiongemma-26b-a4b-it-q4-k-m", [], "19.0G")], 32));

        Assert.Equal(32, row.Length);
        Assert.StartsWith("○ [V] diff", row);
        Assert.Contains("…", row);
        Assert.Contains("q4-k-m", row);
        Assert.EndsWith("19.0G", row);
    }

    [Fact]
    public void ProfileRows_AlignsTagColumnAcrossRowsWithDifferentVisionFlags()
    {
        var rows = UiText.ProfileRows([
            new UiText.ProfileRowData("○", false, "alpha", ["fast"], "2.3G"),
            new UiText.ProfileRowData("●", true, "beta", ["vision", "big model"], "21.3G")
        ], 40);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(40, r.Length));
        Assert.Equal(rows[0].IndexOf("fast"), rows[1].IndexOf("vision"));
        Assert.EndsWith("2.3G", rows[0]);
        Assert.EndsWith("21.3G", rows[1]);
    }

    [Fact]
    public void ProfileRows_TruncatesTagsBeforeTruncatingNames()
    {
        var rows = UiText.ProfileRows([
            new UiText.ProfileRowData("○", false, "short-name", ["a-very-long-tag-that-will-not-fit-in-this-narrow-panel-at-all"], "2.3G")
        ], 30);

        var row = Assert.Single(rows);
        Assert.StartsWith("○     short-name ", row);
        Assert.Contains("…", row);
        Assert.EndsWith("2.3G", row);
    }

    [Fact]
    public void ProfileRows_KeepsFullTagsWhenTheyFit()
    {
        var row = Assert.Single(UiText.ProfileRows([new UiText.ProfileRowData("○", false, "alpha", ["fast", "coding"], "2.3G")], 50));

        Assert.Equal(50, row.Length);
        Assert.Contains("fast, coding", row);
        Assert.DoesNotContain("…", row);
    }

    [Fact]
    public void RelativeTime_UsesCompactOperatorFriendlyText()
    {
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("18m ago", UiText.RelativeTime(now.AddMinutes(-18), now));
        Assert.Equal("3h ago", UiText.RelativeTime(now.AddHours(-3), now));
    }

    [Fact]
    public void RequestMetrics_LabelsTheLatestRequestThroughputAndOutput()
    {
        var stats = new ServerStats();
        stats.Consume("prompt eval time =     810.49 ms /   114 tokens (    7.11 ms per token,   140.66 tokens per second)");
        stats.Consume("3.43.918.536 I slot print_timing: id 0 | task 1587 | n_decoded = 399, tg = 19.80 t/s, tg_3s = 19.61 t/s");

        var text = UiText.RequestMetrics(stats);

        Assert.Equal("Input   140.7 tok/s  ·  114 tokens\nOutput  19.8 tok/s  ·  avg at start: 19.80 tok/s\nStats   399 output tokens", text);
    }

    [Fact]
    public void RequestMetrics_ExplainsWhenNoRequestHasProducedMetrics()
    {
        Assert.Equal("Request stats  Waiting for the first request…", UiText.RequestMetrics(new ServerStats()));
    }

    [Fact]
    public void RequestMetrics_ShowsLivePromptProcessingProgress()
    {
        var stats = new ServerStats();
        stats.Consume("prompt processing, n_tokens = 2560, progress = 0.32, t = 10.00 s / 256.08 tokens per second");

        Assert.Equal("Input   reading 32 %  ·  2,560 tokens  ·  256.1 tok/s\nOutput  waiting for generation…", UiText.RequestMetrics(stats));
    }

}
