using Xunit;

public sealed class RunResourceGraphTests
{
    [Fact]
    public void FormatsVramAndSystemRamPeaks()
    {
        var gib = 1024L * 1024 * 1024;
        var samples = new[]
        {
            new RunResourceSample { Timestamp = DateTimeOffset.Now, VramUsedBytes = 8 * gib, VramTotalBytes = 16 * gib, SystemRamUsedBytes = 20 * gib, SystemRamTotalBytes = 32 * gib },
            new RunResourceSample { Timestamp = DateTimeOffset.Now.AddSeconds(2), VramUsedBytes = 15 * gib, VramTotalBytes = 16 * gib, SystemRamUsedBytes = 31 * gib, SystemRamTotalBytes = 32 * gib }
        };

        var graph = RunResourceGraph.Format("qwen", null, samples, 80, live: false);

        Assert.Contains("VRAM  peak 15.0/16.0 GiB (94%)", graph);
        Assert.Contains("SYS RAM  peak 31.0/32.0 GiB (97%)", graph);
        Assert.Contains("#", graph);
    }

    [Fact]
    public void ExplainsWhenOldRunHasNoSamples()
    {
        var graph = RunResourceGraph.Format("qwen", new RunRecord(), [], 80, live: false);

        Assert.Contains("No resource samples", graph);
    }

    [Fact]
    public void ShowsGraphDataPath()
    {
        var started = DateTimeOffset.Now;
        var samples = new[] { new RunResourceSample { Timestamp = started, VramUsedBytes = 8, VramTotalBytes = 16 } };

        var graph = RunResourceGraph.Format("qwen", null, samples, 80, live: true, dataPath: "/runs/run-qwen.dat");

        Assert.Contains("Data file: /runs/run-qwen.dat", graph);
    }
}
