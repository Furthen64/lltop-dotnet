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
    public void HelpfulAutofitWarningIsNotAnError()
    {
        var parsed = LlamaLogParser.Parse("W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 99, abort.");
        Assert.Equal("gpu_layers_autofit_skipped", parsed.HintKind);
        Assert.False(parsed.IsError);
    }
}
