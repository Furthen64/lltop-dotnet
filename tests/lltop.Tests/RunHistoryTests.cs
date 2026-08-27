using Xunit;

public sealed class RunHistoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "lltop-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavesLoadsAndSummarizesRuns()
    {
        var profile = new Profile { Name = "qwen", Model = "/m.gguf" };
        var stats = new ServerStats();
        stats.Consume("eval time =  100.00 ms /   10 tokens (  10.00 ms per token,   100.00 tokens per second)");
        RunHistory.Save(dir, RunRecord.Create(profile, "server", DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now, 0, "exit", stats));
        var summary = RunHistory.Summarize(dir, "qwen");
        Assert.Equal(1, summary.RunCount);
        Assert.Equal(100, summary.Generation.Latest);
        Assert.Equal(0, summary.LastExitCode);
        Assert.NotNull(summary.LastRunAt);
        Assert.NotEmpty(RunHistory.Sparkline(summary.Generation.Series));
        Assert.Contains("\"model\"", File.ReadAllText(Directory.GetFiles(dir, "*.json").Single()));
    }

    [Fact]
    public void FindsRecentFailureForSameScenario()
    {
        var profile = new Profile { Name = "qwen", Model = "/m.gguf" };
        RunHistory.Save(dir, RunRecord.Create(profile, "server", DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now.AddSeconds(-1), 1, "exit", new ServerStats()));
        Assert.NotNull(RunHistory.FindRecentFailure(dir, profile, 120, 20));
        profile.Ctx++;
        Assert.Null(RunHistory.FindRecentFailure(dir, profile, 120, 20));
    }

    [Fact]
    public void RecognizesPreviousRunOnlyForTheSameScenario()
    {
        var profile = new Profile { Name = "qwen", Model = "/m.gguf" };
        RunHistory.Save(dir, RunRecord.Create(profile, "server", DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now, 0, "exit", new ServerStats()));

        Assert.True(RunHistory.HasRunForScenario(dir, profile));
        profile.Ngl++;
        Assert.False(RunHistory.HasRunForScenario(dir, profile));
    }

    [Fact]
    public void DuplicateProfile_IsTreatedAsFirstLaunchEvenWhenItsScenarioWasRun()
    {
        var original = new Profile { Name = "qwen", Model = "/m.gguf" };
        RunHistory.Save(dir, RunRecord.Create(original, "server", DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now, 0, "exit", new ServerStats()));
        var duplicate = original.Copy("qwen-copy");

        Assert.True(RunHistory.HasRunForScenario(dir, duplicate));
        Assert.False(RunHistory.HasRunForProfile(dir, duplicate.Name));
    }

    [Fact]
    public void SavesResourceSamplesWithRun()
    {
        var profile = new Profile { Name = "qwen", Model = "/m.gguf" };
        var sample = new RunResourceSample
        {
            Timestamp = DateTimeOffset.Now,
            VramUsedBytes = 8L * 1024 * 1024 * 1024,
            VramTotalBytes = 16L * 1024 * 1024 * 1024,
            SystemRamUsedBytes = 31L * 1024 * 1024 * 1024,
            SystemRamTotalBytes = 32L * 1024 * 1024 * 1024
        };
        RunHistory.Save(dir, RunRecord.Create(profile, "server", sample.Timestamp, sample.Timestamp, 1, "exit", new ServerStats(), resourceSamples: [sample]));

        var run = Assert.Single(RunHistory.ForProfile(dir, profile.Name)).Record;
        Assert.Single(run.ResourceSamples);
        Assert.Equal(sample.SystemRamUsedBytes, run.ResourceSamples[0].SystemRamUsedBytes);
    }

    [Fact]
    public void SavesGraphDataPathWithRun()
    {
        var profile = new Profile { Name = "qwen", Model = "/m.gguf" };
        var graphDataPath = Path.Combine(dir, "run-20260828-120000-qwen.dat");
        RunHistory.Save(dir, RunRecord.Create(profile, "server", DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now, 0, "exit", new ServerStats(), graphDataPath: graphDataPath));

        var run = Assert.Single(RunHistory.ForProfile(dir, profile.Name)).Record;
        Assert.Equal(graphDataPath, run.GraphDataPath);
    }

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
