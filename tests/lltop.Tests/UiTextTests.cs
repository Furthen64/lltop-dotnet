using Xunit;

public sealed class UiTextTests
{
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
}
