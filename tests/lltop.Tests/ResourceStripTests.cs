using Xunit;

public sealed class ResourceStripTests
{
    private const long GiB = 1024L * 1024L * 1024L;

    [Fact]
    public void PercentageCalculationsUseUsedOverTotalAndClamp()
    {
        Assert.Equal(50, ResourceStripFormatter.CalculatePercentage(5, 10));
        Assert.Equal(100, ResourceStripFormatter.CalculatePercentage(12, 10));
        Assert.Null(ResourceStripFormatter.CalculatePercentage(5, 0));
        Assert.Null(ResourceStripFormatter.CalculatePercentage(null, 10));

        var previous = new CpuTimes(200, 1_000);
        var current = new CpuTimes(250, 1_200);
        Assert.Equal(75, LinuxSystemResourceProvider.CalculateCpuUsagePercent(previous, current));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(74.9, 0)]
    [InlineData(75.0, 1)]
    [InlineData(89.9, 1)]
    [InlineData(90.0, 2)]
    [InlineData(100.0, 2)]
    public void ThresholdSelectionMatchesOperationalBands(double? percentage, int expected)
    {
        Assert.Equal((ResourceThreshold)expected, ResourceStripFormatter.ThresholdFor(percentage));
    }

    [Theory]
    [InlineData(0, 4, "[░░░░]")]
    [InlineData(50, 4, "[██░░]")]
    [InlineData(75, 4, "[███░]")]
    [InlineData(100, 4, "[████]")]
    public void ProgressBarRendersFilledAndRemainingCells(double percentage, int width, string expected)
    {
        Assert.Equal(expected, ResourceStripFormatter.ProgressBar(percentage, width));
    }

    [Fact]
    public void UnavailableMetricsRenderAsNaRatherThanFailing()
    {
        var content = ResourceStripFormatter.Format(new SystemResourceSnapshot { RunningServerCount = 0 }, 120);

        Assert.Contains("VRAM N/A", content.Text);
        Assert.Contains("GPU N/A", content.Text);
        Assert.Contains("RAM N/A", content.Text);
        Assert.Contains("CPU N/A", content.Text);
        Assert.EndsWith("0 RUNNING", content.Text);
    }

    [Fact]
    public void NarrowFormattingPreservesEssentialPercentagesAndRunningCount()
    {
        var snapshot = SampleSnapshot();

        var content = ResourceStripFormatter.Format(snapshot, 40);

        Assert.True(content.Text.Length <= 40);
        Assert.Contains("V 69%", content.Text);
        Assert.Contains("R 59%", content.Text);
        Assert.Contains("C 24%", content.Text);
        Assert.Contains("2 RUN", content.Text);
        Assert.DoesNotContain("RTX", content.Text);
        Assert.DoesNotContain("/", content.Text);
    }

    [Fact]
    public void WideFormattingIncludesBarsValuesAndOperationalOrder()
    {
        var text = ResourceStripFormatter.Format(SampleSnapshot(), 140).Text;

        Assert.Contains("VRAM [", text);
        Assert.Contains("22.0/32.0G 69%", text);
        Assert.Contains("RAM [", text);
        Assert.Contains("8.3/14.0G 59%", text);
        Assert.True(text.IndexOf("VRAM", StringComparison.Ordinal) < text.IndexOf(" | GPU", StringComparison.Ordinal));
        Assert.True(text.IndexOf(" | GPU", StringComparison.Ordinal) < text.IndexOf(" | RAM", StringComparison.Ordinal));
        Assert.True(text.IndexOf(" | RAM", StringComparison.Ordinal) < text.IndexOf(" | CPU", StringComparison.Ordinal));
        Assert.EndsWith("2 RUNNING", text);
    }

    [Fact]
    public void ProcParsersReadLinuxUnitsAndCpuCounters()
    {
        Assert.Equal(16L * 1024L, LinuxSystemResourceProvider.ParseMemInfoBytes("MemTotal:       16 kB"));
        Assert.Equal(new CpuTimes(45, 150), LinuxSystemResourceProvider.ParseCpuTimes("cpu  10 20 30 40 5 15 20 10"));
        Assert.Null(LinuxSystemResourceProvider.ParseCpuTimes("cpu malformed"));
    }

    private static SystemResourceSnapshot SampleSnapshot() => new()
    {
        VramUsedBytes = 22 * GiB,
        VramTotalBytes = 32 * GiB,
        GpuUsagePercent = 68,
        GpuName = "NVIDIA GeForce RTX 4090",
        SystemRamUsedBytes = (long)(8.3 * GiB),
        SystemRamTotalBytes = 14 * GiB,
        CpuUsagePercent = 24,
        RunningServerCount = 2
    };
}
