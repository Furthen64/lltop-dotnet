using System.Net;
using System.Text;
using Xunit;

public sealed class OpenCodeContextSyncTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "lltop-opencode-sync-" + Guid.NewGuid().ToString("N"));
    readonly string configPath;

    public OpenCodeContextSyncTests()
    {
        Directory.CreateDirectory(root);
        configPath = Path.Combine(root, "opencode.json");
    }

    [Fact]
    public void MatchingContextsDoNotRequestAnUpdate()
    {
        Assert.False(OpenCodeContextSync.NeedsUpdate(45_056, 45_056));
    }

    [Theory]
    [InlineData(65_536, 192_000)]
    [InlineData(192_000, 65_536)]
    public void DifferentContextsRequestAnUpdateInEitherDirection(long configured, long effective)
    {
        Assert.True(OpenCodeContextSync.NeedsUpdate(configured, effective));
    }

    [Fact]
    public void DecliningLeavesFileUnchangedAndCreatesNoBackup()
    {
        var original = Encoding.UTF8.GetBytes(ConfigJson);
        File.WriteAllBytes(configPath, original);
        var snapshot = OpenCodeContextSync.ReadConfig(configPath);

        var result = OpenCodeContextSync.ApplyDecision(snapshot, 192_000, approved: false);

        Assert.Null(result);
        Assert.Equal(original, File.ReadAllBytes(configPath));
        Assert.Empty(Directory.GetFiles(root, "*.bak"));
    }

    [Fact]
    public void ApprovalChangesOnlyContextAndBacksUpExactOriginalBytes()
    {
        var original = Encoding.UTF8.GetBytes(ConfigJson);
        File.WriteAllBytes(configPath, original);
        var snapshot = OpenCodeContextSync.ReadConfig(configPath);

        var update = OpenCodeContextSync.ApplyDecision(snapshot, 192_000, approved: true);

        Assert.NotNull(update);
        Assert.Equal(original, File.ReadAllBytes(update.BackupPath));
        var changed = File.ReadAllText(configPath);
        Assert.Contains("\"context\": 192000", changed);
        Assert.Contains("\"output\": 8192", changed);
        Assert.Contains("\"url\": \"http://127.0.0.1:8080\"", changed);
        Assert.Contains("\"117_local\"", changed);
        Assert.Contains("\"apiKey\": \"keep-me\"", changed);
        Assert.Single(Directory.GetFiles(root, "opencodejson_*.bak"));
    }

    [Fact]
    public void ConcurrentModificationAbortsWithoutReplacingNewerBytes()
    {
        File.WriteAllText(configPath, ConfigJson);
        var snapshot = OpenCodeContextSync.ReadConfig(configPath);
        var newer = Encoding.UTF8.GetBytes(ConfigJson.Replace("65536", "65537", StringComparison.Ordinal));
        File.WriteAllBytes(configPath, newer);

        var error = Assert.Throws<InvalidOperationException>(() => OpenCodeContextSync.UpdateConfig(snapshot, 192_000));

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(newer, File.ReadAllBytes(configPath));
        Assert.Empty(Directory.GetFiles(root, "*.bak"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"id\":0,\"n_ctx\":0}]")]
    [InlineData("[{\"id\":0}]")]
    [InlineData("not json")]
    public void InvalidSlotsResponseFailsSafely(string json)
    {
        Assert.ThrowsAny<Exception>(() => OpenCodeContextSync.ParseSlotsContext(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void IdenticalMultiSlotContextsAreAcceptedAsOnePerRequestContext()
    {
        var context = OpenCodeContextSync.ParseSlotsContext(Encoding.UTF8.GetBytes("[{\"n_ctx\":45056},{\"n_ctx\":45056}]"));

        Assert.Equal(45_056, context);
    }

    [Fact]
    public void DifferentMultiSlotContextsAreReportedAsAmbiguous()
    {
        var error = Assert.Throws<InvalidDataException>(() => OpenCodeContextSync.ParseSlotsContext(
            Encoding.UTF8.GetBytes("[{\"n_ctx\":45056},{\"n_ctx\":65536}]")));

        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewLaunchInvalidatesOldResultAndEachLaunchCanPromptOnlyOnce()
    {
        var launches = new OpenCodeContextLaunchState();
        var first = launches.BeginLaunch();
        Assert.True(launches.TryClaimPrompt(first));
        Assert.False(launches.TryClaimPrompt(first));

        var second = launches.BeginLaunch();

        Assert.False(launches.IsCurrent(first));
        Assert.False(launches.TryClaimPrompt(first));
        Assert.True(launches.IsCurrent(second));
        Assert.True(launches.TryClaimPrompt(second));
        Assert.False(launches.TryClaimPrompt(second));
    }

    [Fact]
    public async Task UnavailableSlotsEndpointFailsAfterConfiguredShortTimeout()
    {
        var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var sync = new OpenCodeContextSync(client, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.WaitForEffectiveContextAsync(8080, CancellationToken.None));

        Assert.Contains("/slots returned 503", error.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"provider\":{\"66_local\":{\"models\":{}}}}")]
    public void MissingOrMalformedOpenCodeConfigurationFailsSafely(string content)
    {
        File.WriteAllText(configPath, content);

        Assert.Throws<InvalidDataException>(() => OpenCodeContextSync.ReadConfig(configPath));
    }

    [Fact]
    public void MissingOpenCodeConfigurationFailsSafely()
    {
        Assert.Throws<InvalidDataException>(() => OpenCodeContextSync.ReadConfig(configPath));
    }

    [Fact]
    public void BackupNameCollisionDoesNotOverwriteExistingBackup()
    {
        File.WriteAllText(configPath, ConfigJson);
        var snapshot = OpenCodeContextSync.ReadConfig(configPath);
        var first = OpenCodeContextSync.UpdateConfig(snapshot, 65_537);
        var collisionContents = Encoding.UTF8.GetBytes("existing backup");
        var stampName = Path.GetFileName(first.BackupPath);
        var collisionPath = Path.Combine(root, stampName);

        // Force the next backup path to be taken if the clock has the same second.
        File.WriteAllBytes(collisionPath, collisionContents);
        var current = OpenCodeContextSync.ReadConfig(configPath);
        var second = OpenCodeContextSync.UpdateConfig(current, 65_538);

        Assert.Equal(collisionContents, File.ReadAllBytes(collisionPath));
        Assert.NotEqual(collisionPath, second.BackupPath);
    }

    const string ConfigJson = "{\n  \"provider\": {\n    \"66_local\": {\n      \"models\": {\n        \"qwen\": {\n          \"limit\": {\n            \"context\": 65536,\n            \"output\": 8192\n          },\n          \"metadata\": {\"keep\": true}\n        }\n      },\n      \"url\": \"http://127.0.0.1:8080\",\n      \"apiKey\": \"keep-me\"\n    },\n    \"117_local\": {\"models\": {\"leave\": true}}\n  },\n  \"agent\": {\"keep\": true}\n}\n";

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => response(request, cancellationToken);
    }
}
