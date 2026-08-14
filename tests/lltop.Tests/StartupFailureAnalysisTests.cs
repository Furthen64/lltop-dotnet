using Xunit;

public sealed class StartupFailureAnalysisTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "lltop-analysis-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplainsDiffusionModelAndReadsTheLinkedLog()
    {
        Directory.CreateDirectory(dir);
        var model = Path.Combine(dir, "diffusion.gguf");
        File.WriteAllText(model, "not a gguf");
        var log = Path.Combine(dir, "failed.log");
        File.WriteAllText(log, "srv    load_model: failed to load model, 'diffusion.gguf'\n");
        var profile = new Profile { Name = "diffusion", Model = model, Ngl = 0, NoMmap = true };
        var run = RunRecord.Create(profile, "server", DateTimeOffset.Now.AddSeconds(-1), DateTimeOffset.Now, 1, "exit", new ServerStats(), log);

        var result = StartupFailureAnalysis.Create(profile, run, dir);

        Assert.Contains("diffusion-style model", result);
        Assert.Contains("CPU-only", result);
        Assert.Contains("Memory mapping is disabled", result);
        Assert.Contains("failed to load model", result);
    }

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}
