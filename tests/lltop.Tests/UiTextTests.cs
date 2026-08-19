using Xunit;

public sealed class UiTextTests
{
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
    public void ProfileRow_PreservesMarkerVisionSuffixAndBothEndsOfName()
    {
        var row = UiText.ProfileRow("○", true, "diffusiongemma-26b-a4b-it-q4-k-m", "19.0G", 32);

        Assert.Equal(32, row.Length);
        Assert.StartsWith("○ [V] diff", row);
        Assert.Contains("…", row);
        Assert.Contains("q4-k-m", row);
        Assert.EndsWith("19.0G", row);
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

        Assert.Equal("Latest request  input 140.7 tok/s  ·  output 19.8 tok/s  ·  399 output tokens", text);
        Assert.DoesNotContain("prompt", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestMetrics_ExplainsWhenNoRequestHasProducedMetrics()
    {
        Assert.Equal("Request stats  Waiting for the first request…", UiText.RequestMetrics(new ServerStats()));
    }

}
