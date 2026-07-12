using Xunit;

public sealed class ServerCapabilityCompatibilityTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "lltop-capability-tests-" + Guid.NewGuid().ToString("N"));

    const string LegacyVersion = """
        llama.cpp server version 7840
        commit: b0311c16d
        backend: CUDA
        GPU: NVIDIA GeForce GTX 1050 Ti
        compute capability: 6.1
        """;

    const string LegacyHelp = """
        usage: llama-server [options]
          -m, --model MODEL              model path
          --host HOST                    host to bind
          --port PORT                    port to bind
          -a ALIAS                       alias
          -c N                           context size
          -ngl N                         GPU layers
          --temp N                       temperature
          --top-p N                      top p
          --top-k N                      top k
          --min-p N                      min p
          -b N                           batch size
          -ub N                          micro batch size
          --parallel N                   parallel slots
          --threads N                    CPU threads
          --metrics                      enable metrics
          --no-mmap                      disable mmap
          --chat-template TEMPLATE       force chat template
          --device DEVICE                pick device
          --tensor-split SPLIT           tensor split
        """;

    const string ModernVersion = """
        llama.cpp server version 8123
        commit: deadbeef12
        backend: CUDA
        GPU: NVIDIA GeForce RTX 4090
        compute capability: 8.9
        """;

    const string ModernHelp = """
        usage: llama-server [options]
          -m, --model MODEL              model path
          --host HOST                    host to bind
          --port PORT                    port to bind
          -a ALIAS                       alias
          -c N                           context size
          -ngl N                         GPU layers
          --temp N                       temperature
          --top-p N                      top p
          --top-k N                      top k
          --min-p N                      min p
          -b N                           batch size
          -ub N                          micro batch size
          --parallel N                   parallel slots
          --threads N                    CPU threads
          --flash-attn VALUE             flash attention
          --metrics                      enable metrics
          --jinja                        enable jinja
          --no-mmap                      disable mmap
          --chat-template TEMPLATE       force chat template
          --reasoning MODE               reasoning mode
          --reasoning-budget N           reasoning budget
          --device DEVICE                pick device
          --tensor-split SPLIT           tensor split
        """;

    [Fact]
    public void OlderHelp_DoesNotReportReasoningSupport()
    {
        var capability = Capability(LegacyVersion, LegacyHelp);
        Assert.False(capability.SupportsOption("--reasoning"));
        Assert.Equal("legacy", capability.CompatibilityMode);
    }

    [Fact]
    public void NewerHelp_ReportsReasoningSupport()
    {
        var capability = Capability(ModernVersion, ModernHelp);
        Assert.True(capability.SupportsOption("--reasoning"));
        Assert.Equal("native", capability.CompatibilityMode);
    }

    [Fact]
    public void UnsupportedFlagWithSeparateValue_RemovesOptionAndValue()
    {
        var profile = BaseProfile();
        profile.Reasoning = "auto";

        var plan = ServerRunner.BuildLaunchPlan("/llama-server", profile, Capability(LegacyVersion, LegacyHelp));

        Assert.DoesNotContain("--reasoning", plan.FilteredArguments);
        Assert.DoesNotContain("auto", plan.FilteredArguments);
        Assert.Contains(plan.RemovedArguments, x => x.OptionName == "--reasoning");
    }

    [Fact]
    public void UnsupportedFlagWithEquals_RemovesWholeToken()
    {
        var profile = BaseProfile();
        profile.ExtraArgs = ["--reasoning=auto"];

        var plan = ServerRunner.BuildLaunchPlan("/llama-server", profile, Capability(LegacyVersion, LegacyHelp));

        Assert.DoesNotContain("--reasoning=auto", plan.FilteredArguments);
        Assert.Contains(plan.RemovedArguments, x => x.Display == "--reasoning=auto" && x.FromManualArgs);
    }

    [Fact]
    public void SupportedShortOption_IsPreserved()
    {
        var profile = BaseProfile();
        profile.ExtraArgs = ["-c", "4096"];

        var plan = ServerRunner.BuildLaunchPlan("/llama-server", profile, Capability(LegacyVersion, LegacyHelp));

        Assert.Contains("-c", plan.FilteredArguments);
        Assert.Contains("4096", plan.FilteredArguments);
    }

    [Fact]
    public void BooleanOption_DoesNotConsumeFollowingPositionalValue()
    {
        var profile = BaseProfile();
        profile.ExtraArgs = ["--jinja", "/tmp/prompt.txt"];

        var plan = ServerRunner.BuildLaunchPlan("/llama-server", profile, Capability(LegacyVersion, LegacyHelp));

        Assert.DoesNotContain("--jinja", plan.FilteredArguments);
        Assert.Contains("/tmp/prompt.txt", plan.FilteredArguments);
    }

    [Fact]
    public void RepeatedOptions_ArePreservedWhenSupported()
    {
        var profile = BaseProfile();
        profile.ExtraArgs = ["--device", "0", "--device", "1"];

        var plan = ServerRunner.BuildLaunchPlan("/llama-server", profile, Capability(ModernVersion, ModernHelp));

        Assert.Equal(2, plan.FilteredArguments.Count(x => x == "--device"));
        Assert.Contains("0", plan.FilteredArguments);
        Assert.Contains("1", plan.FilteredArguments);
    }

    [Fact]
    public void NegativeNumericValue_IsNotTreatedAsOptionName()
    {
        var profile = BaseProfile();
        profile.ExtraArgs = ["--tensor-split", "-1"];

        var plan = ServerRunner.BuildLaunchPlan("/llama-server", profile, Capability(ModernVersion, ModernHelp));

        Assert.Contains("--tensor-split", plan.FilteredArguments);
        Assert.Contains("-1", plan.FilteredArguments);
    }

    [Fact]
    public void ManuallyAuthoredUnsupportedOptions_AreSurfaced()
    {
        var profile = BaseProfile();
        profile.ExtraArgs = ["--reasoning", "auto"];

        var plan = ServerRunner.BuildLaunchPlan("/llama-server", profile, Capability(LegacyVersion, LegacyHelp));

        Assert.True(plan.HasManualRemovals);
        Assert.Contains(plan.RemovedArguments, x => x.Display == "--reasoning auto");
    }

    [Fact]
    public void CacheInvalidatesWhenPathOrModificationTimeChanges()
    {
        var cachePath = Path.Combine(root, "cache.json");
        var executor = new FakeProbeExecutor();
        var first = Touch("first-server");
        var second = Touch("second-server");
        executor.Add(first, "--version", ProbeCommandResult.Success(LegacyVersion));
        executor.Add(first, "--help", ProbeCommandResult.Success(LegacyHelp));
        executor.Add(second, "--version", ProbeCommandResult.Success(ModernVersion));
        executor.Add(second, "--help", ProbeCommandResult.Success(ModernHelp));

        var cache = new ServerCapabilityCache(cachePath, executor);
        _ = cache.Get(first);
        _ = cache.Get(first);
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
        _ = cache.Get(first);
        _ = cache.Get(second);

        Assert.Equal(6, executor.Calls.Count);
        Assert.Equal(4, executor.Calls.Count(x => x == $"{first} --version" || x == $"{first} --help"));
        Assert.Equal(2, executor.Calls.Count(x => x == $"{second} --version" || x == $"{second} --help"));
    }

    [Fact]
    public void PascalDetection_AppliesLegacyDefaults()
    {
        var capability = Capability(LegacyVersion, LegacyHelp);
        Assert.True(capability.IsPascalCuda);
        var profile = FirstRunProfiles.CreateForModel(Config(), "qwen", Path.Combine(root, "Qwen3.gguf"), capability);

        Assert.Equal(4096, profile.Ctx);
        Assert.Equal(256, profile.Batch);
        Assert.Equal(128, profile.UBatch);
        Assert.Equal(1, profile.Parallel);
        Assert.Equal("", profile.FlashAttn);
    }

    [Fact]
    public void NonPascalGpu_DoesNotApplyLegacyDefaults()
    {
        var profile = FirstRunProfiles.CreateForModel(Config(), "qwen", Path.Combine(root, "Qwen3.gguf"), Capability(ModernVersion, ModernHelp));

        Assert.Equal(65536, profile.Ctx);
        Assert.Equal(512, profile.Batch);
        Assert.Equal(256, profile.UBatch);
        Assert.Equal("auto", profile.Reasoning);
    }

    [Fact]
    public void ProbeFailure_UsesSafeFallbackAndRemovesModernFlags()
    {
        var cachePath = Path.Combine(root, "fallback-cache.json");
        var binary = Touch("broken-server");
        var executor = new FakeProbeExecutor();
        executor.Add(binary, "--version", ProbeCommandResult.Failure("permission denied"));
        executor.Add(binary, "--help", ProbeCommandResult.Failure("permission denied"));
        var cache = new ServerCapabilityCache(cachePath, executor);

        var capability = cache.Get(binary);
        var plan = ServerRunner.BuildLaunchPlan(binary, BaseProfile(), capability);

        Assert.False(capability.ProbeSucceeded);
        Assert.True(capability.DetectionIncomplete);
        Assert.Contains("-m", plan.FilteredArguments);
        Assert.Contains("--port", plan.FilteredArguments);
        Assert.DoesNotContain("--reasoning", plan.FilteredArguments);
    }

    Profile BaseProfile() => new()
    {
        Name = "test",
        Model = "/models/test.gguf",
        Port = 8080,
        Ctx = 4096,
        Ngl = 16,
        Batch = 256,
        UBatch = 128,
        Parallel = 1,
        Metrics = true,
        Jinja = true,
        NoMmap = true,
        ChatTemplate = "chatml",
        Reasoning = "auto"
    };

    AppConfig Config() => new()
    {
        LlamaServer = "/llama-server",
        ModelsDir = root,
        ProfilesDir = Path.Combine(root, "profiles")
    };

    ServerCapabilityRecord Capability(string versionText, string helpText)
    {
        var path = Touch(Guid.NewGuid().ToString("N"));
        return ServerCapabilityParser.Parse(
            path,
            new FileInfo(path),
            ProbeCommandResult.Success(versionText),
            ProbeCommandResult.Success(helpText));
    }

    string Touch(string name)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "binary");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    sealed class FakeProbeExecutor : IServerProbeExecutor
    {
        readonly Dictionary<(string Path, string Argument), ProbeCommandResult> results = new();
        public List<string> Calls { get; } = [];

        public void Add(string path, string argument, ProbeCommandResult result) => results[(path, argument)] = result;

        public ProbeCommandResult Run(string executable, string argument)
        {
            Calls.Add($"{executable} {argument}");
            return results.TryGetValue((executable, argument), out var result)
                ? result
                : ProbeCommandResult.Failure("missing fixture");
        }
    }
}
