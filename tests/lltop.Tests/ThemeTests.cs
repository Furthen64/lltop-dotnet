using Xunit;

public sealed class ThemeTests
{
    [Fact]
    public void Midnight_IsTheDefaultNamedTheme()
    {
        Assert.True(LltopTheme.Select("midnight"));

        Assert.Equal("Midnight", LltopTheme.CurrentName);
        Assert.Contains("Midnight", LltopTheme.Names);
        Assert.NotEqual(LltopTheme.Warning, LltopTheme.Error);
        Assert.NotEqual(LltopTheme.MemoryFullyOnGpu, LltopTheme.MemoryPartialOffload);
    }

    [Fact]
    public void UnknownTheme_FallsBackToMidnight()
    {
        Assert.False(LltopTheme.Select("not-a-theme"));

        Assert.Equal("Midnight", LltopTheme.CurrentName);
    }
}

