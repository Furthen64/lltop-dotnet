using Xunit;

public sealed class LogLineStyleTests
{
    [Theory]
    [InlineData("ordinary server output", "Normal")]
    [InlineData("ERROR unable to start", "Error")]
    [InlineData("model load failed", "Error")]
    [InlineData("warning: context reduced", "Warning")]
    [InlineData("eval: 42.0 tokens per second", "Performance")]
    [InlineData("load_tensors: offloaded 26/49 layers to GPU", "Offload")]
    [InlineData("prompt processing progress = 0.5", "Progress")]
    public void ClassifyMatchesGoColorRules(string line, string expected)
    {
        Assert.Equal(expected, LogLineStyle.Classify(line).ToString());
    }

    [Fact]
    public void KnownAutofitHintIsWarningInsteadOfError()
    {
        const string line = "W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 99, abort.";

        Assert.Equal(LogLineKind.Hint, LogLineStyle.Classify(line));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal(LogLineKind.Warning, LogLineStyle.Classify("WARN: CHECK THIS"));
    }
}
