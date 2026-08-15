using Xunit;

public sealed class LogLineStyleTests
{
    [Theory]
    [InlineData("ordinary server output", "Normal")]
    [InlineData("ERROR unable to start", "Error")]
    [InlineData("model load failed", "Error")]
    [InlineData("warning: context reduced", "Warning")]
    [InlineData("eval: 42.0 tokens per second", "Performance")]
    [InlineData("n_decoded = 399, tg = 19.80 t/s, tg_3s = 19.61 t/s", "Performance")]
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

    [Theory]
    [InlineData("0.00.189.231 W common_fit_params: loading warning", "Warning")]
    [InlineData("0.00.307.681 E srv load_model: rejected architecture", "Error")]
    [InlineData("0.00.001.000 I srv listening on port 8080", "Normal")]
    public void ClassifiesLlamaSeverityPrefix(string line, string expected)
    {
        Assert.Equal(expected, LogLineStyle.Classify(line).ToString());
    }
}
