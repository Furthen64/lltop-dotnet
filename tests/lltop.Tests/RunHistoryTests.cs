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

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
