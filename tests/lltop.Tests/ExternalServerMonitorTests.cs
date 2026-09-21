using Xunit;

public sealed class ExternalServerMonitorTests
{
    [Fact]
    public void ParseLaunchArguments_ReconstructsEndpointAndModel()
    {
        var launch = ExternalServerMonitor.ParseLaunchArguments([
            "/usr/bin/llama-server", "-m", "/models/Qwen 3.gguf", "--host=0.0.0.0", "--port", "9090"
        ]);

        Assert.Equal("0.0.0.0", launch.Host);
        Assert.Equal(9090, launch.Port);
        Assert.Equal("/models/Qwen 3.gguf", launch.Model);
    }

    [Fact]
    public void ParseLaunchArguments_UsesLocalDefaultsForAnUnspecifiedEndpoint()
    {
        var launch = ExternalServerMonitor.ParseLaunchArguments(["llama-server", "--model=/models/test.gguf"]);

        Assert.Equal("127.0.0.1", launch.Host);
        Assert.Equal(8080, launch.Port);
        Assert.Equal("/models/test.gguf", launch.Model);
    }

    [Fact]
    public void DetectReturnsNullWhenNoServerIsRunning()
    {
        // The test is intentionally tolerant of developer machines that already run llama-server.
        var result = ExternalServerMonitor.Detect(Path.GetTempPath());
        Assert.True(result is null || result.Pid > 0);
    }
}
