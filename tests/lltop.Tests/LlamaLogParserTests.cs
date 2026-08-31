using Xunit;

public sealed class LlamaLogParserTests
{
    [Fact]
    public void ParsesPromptAndEvalThroughput()
    {
        var prompt = LlamaLogParser.Parse("prompt eval time =     810.49 ms /   114 tokens (    7.11 ms per token,   140.66 tokens per second)");
        var eval = LlamaLogParser.Parse("eval time =  119350.76 ms /   449 tokens (  265.81 ms per token,     3.76 tokens per second)");
        Assert.Equal(140.66, prompt.PromptTokensPerSecond);
        Assert.Equal(114, prompt.PromptTokens);
        Assert.Equal(0, prompt.EvalTokensPerSecond);
        Assert.Equal(3.76, eval.EvalTokensPerSecond);
        Assert.Equal(449, eval.EvalTokens);
    }

    [Fact]
    public void ParsesRuntimeAndErrorDetails()
    {
        var offload = LlamaLogParser.Parse("load_tensors: offloaded 26/49 layers to GPU");
        var progress = LlamaLogParser.Parse("prompt processing progress, n_tokens = 9863, batch.n_tokens = 27, progress = 1.000000");
        var error = LlamaLogParser.Parse("CUDA out of memory while loading");
        Assert.Equal(26, offload.OffloadedLayers);
        Assert.Equal(49, offload.TotalLayers);
        Assert.Equal(1, progress.Progress);
        Assert.Equal("cuda_oom", error.ErrorKind);
    }

    [Fact]
    public void ParsesLivePromptProcessingProgress()
    {
        const string line = "1.37.283.140 I slot print_timing: id 0 | task 0 | prompt processing, n_tokens = 2560, progress = 0.32, t = 10.00 s / 256.08 tokens per second";

        var parsed = LlamaLogParser.Parse(line);
        var stats = new ServerStats();
        stats.Consume(line);

        Assert.Equal(2560, parsed.PromptProgressTokens);
        Assert.Equal(0.32, parsed.Progress);
        Assert.Equal(256.08, parsed.PromptProgressTokensPerSecond);
        Assert.Equal(parsed.PromptProgressTokens, stats.PromptProgressTokens);
    }

    [Fact]
    public void ParsesActiveCudaDeviceFromLiveLog()
    {
        var parsed = LlamaLogParser.Parse("0.00.152.688 I - CUDA0 : NVIDIA GeForce RTX 4070 Ti SUPER (15941 MiB, 15132 MiB free)");
        var stats = new ServerStats();
        stats.Consume("0.00.152.688 I - CUDA0 : NVIDIA GeForce RTX 4070 Ti SUPER (15941 MiB, 15132 MiB free)");

        Assert.Equal("CUDA", parsed.RuntimeBackend);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti SUPER", parsed.RuntimeGpuName);
        Assert.Equal("CUDA", stats.RuntimeBackend);
        Assert.Equal(parsed.RuntimeGpuName, stats.RuntimeGpuName);
    }

    [Fact]
    public void IgnoresCudaArchitectureDetailsThatAreNotADevice()
    {
        var parsed = LlamaLogParser.Parse("I system_info: CUDA : ARCHS = 890 | USE_GRAPHS = 1");

        Assert.Equal("", parsed.RuntimeBackend);
        Assert.Equal("", parsed.RuntimeGpuName);
    }

    [Fact]
    public void ParsesLiveGenerationThroughput()
    {
        const string line = "3.43.918.536 I slot print_timing: id 0 | task 1587 | n_decoded = 399, tg = 19.80 t/s, tg_3s = 19.61 t/s";

        var parsed = LlamaLogParser.Parse(line);
        var stats = new ServerStats();
        stats.Consume(line);

        Assert.Equal(399, parsed.DecodedTokens);
        Assert.Equal(19.80, parsed.GenerationTokensPerSecond);
        Assert.Equal(19.61, parsed.GenerationTokensPerSecond3s);
        Assert.Equal(399, stats.GeneratedTokens);
        Assert.Equal(19.80, stats.EvalTokensPerSecond);
        Assert.Equal(19.61, stats.GenerationTokensPerSecond3s);
    }

    [Fact]
    public void AveragesTheFirstTenGenerationRatesForEachRootRequest()
    {
        var stats = new ServerStats();
        stats.Consume("processing task, is_child = 0");
        for (var rate = 1; rate <= 11; rate++)
            stats.Consume($"n_decoded = {rate}, tg = {rate}.0 t/s, tg_3s = {rate}.0 t/s");

        Assert.Equal(5.5, stats.InitialGenerationTokensPerSecond);

        stats.Consume("processing task, is_child = 0");
        stats.Consume("n_decoded = 1, tg = 40.1 t/s, tg_3s = 40.1 t/s");
        stats.Consume("n_decoded = 2, tg = 40.4 t/s, tg_3s = 40.4 t/s");
        Assert.Equal(40.25, stats.InitialGenerationTokensPerSecond);
    }

    [Fact]
    public void HelpfulAutofitWarningIsNotAnError()
    {
        var parsed = LlamaLogParser.Parse("W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 99, abort.");
        Assert.Equal("gpu_layers_autofit_skipped", parsed.HintKind);
        Assert.False(parsed.IsError);
    }
}
