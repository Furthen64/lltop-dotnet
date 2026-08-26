using Xunit;

public sealed class RunGraphDataTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "lltop-graph-data-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesAppendFriendlySamplesAndEvents()
    {
        var started = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        using (var writer = RunGraphDataWriter.Create(dir, new Profile { Name = "Qwen 3" }, started))
        {
            writer.WriteSample(new RunResourceSample { Timestamp = started.AddSeconds(2), CpuUsagePercent = 42.5, VramUsedBytes = 8 });
            writer.WriteEvent(started.AddSeconds(3), new LlamaRuntimeEvent("slots_idle", new Dictionary<string, object?>(), "all slots are idle"));
        }

        var path = Assert.Single(Directory.GetFiles(dir, "run-*.dat"));
        var content = File.ReadAllText(path);
        Assert.Contains("timestamp_utc\ttype\tcpu_percent", content);
        Assert.Contains("\tsample\t42.5", content);
        Assert.Contains("\tslots_idle\t", content);
        Assert.Contains("all slots are idle", content);
    }

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
