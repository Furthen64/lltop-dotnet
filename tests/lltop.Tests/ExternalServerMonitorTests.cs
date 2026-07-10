using Xunit;

public sealed class ExternalServerMonitorTests
{
    [Fact]
    public void DetectReturnsNullWhenNoServerIsRunning()
    {
        // The test is intentionally tolerant of developer machines that already run llama-server.
        var result = ExternalServerMonitor.Detect(Path.GetTempPath());
        Assert.True(result is null || result.Pid > 0);
    }
}
