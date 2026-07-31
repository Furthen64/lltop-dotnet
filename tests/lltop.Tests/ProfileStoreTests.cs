using Xunit;

public sealed class ProfileStoreTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "lltop-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_RoundTripsSnakeCaseSettings()
    {
        var store = new ProfileStore(directory);
        var original = new Profile
        {
            Name = "Qwen Coding", Model = "/models/Qwen3.6-35B-A3B-Q4.gguf", CacheK = "q8_0", CacheV = "f16",
            TopP = .83, MinP = .02, UBatch = 128, FlashAttn = "on", NoMmap = false,
            Vision = true, Mmproj = "/models/mmproj-BF16.gguf",
            RepeatPenalty = 1.15, RepeatLastN = 128, PresencePenalty = .2, FrequencyPenalty = .3,
            ReasoningBudget = 2048, ExtraArgs = ["--verbose", "--log-colors", "value with spaces"]
        };

        store.Save(original);
        var result = store.LoadAll();

        Assert.Empty(result.Errors);
        var loaded = Assert.Single(result.Profiles);
        Assert.Equal("q8_0", loaded.CacheK);
        Assert.Equal("f16", loaded.CacheV);
        Assert.Equal(.83, loaded.TopP);
        Assert.Equal(.02, loaded.MinP);
        Assert.Equal(1.15, loaded.RepeatPenalty);
        Assert.Equal(128, loaded.RepeatLastN);
        Assert.Equal(.2, loaded.PresencePenalty);
        Assert.Equal(.3, loaded.FrequencyPenalty);
        Assert.Equal(128, loaded.UBatch);
        Assert.Equal("on", loaded.FlashAttn);
        Assert.False(loaded.NoMmap);
        Assert.True(loaded.Vision);
        Assert.Equal(original.Mmproj, loaded.Mmproj);
        Assert.Equal(2048, loaded.ReasoningBudget);
        Assert.Equal(original.ExtraArgs, loaded.ExtraArgs);
    }

    [Fact]
    public void Save_RenamesSourceFileAndDeleteRemovesIt()
    {
        var store = new ProfileStore(directory);
        var profile = new Profile { Name = "First", Model = "/tmp/model.gguf" };
        store.Save(profile);
        var firstPath = profile.SourcePath;

        profile.Name = "Second";
        store.Save(profile);

        Assert.False(File.Exists(firstPath));
        Assert.EndsWith("second.toml", profile.SourcePath);
        store.Delete(profile);
        Assert.False(File.Exists(profile.SourcePath));
    }

    [Fact]
    public void LoadAll_ReportsBadProfileWithoutHidingValidProfiles()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "bad.toml"), "name = \"bad\"\nport = nope\n");
        File.WriteAllText(Path.Combine(directory, "good.toml"), "name = \"good\"\nport = 8080\n");

        var result = new ProfileStore(directory).LoadAll();

        Assert.Single(result.Profiles);
        Assert.Single(result.Errors);
        Assert.Contains("bad.toml", result.Errors[0]);
    }

    [Fact]
    public void Save_DoesNotOverwriteAnUnrelatedProfileWithTheSameSlug()
    {
        var store = new ProfileStore(directory);
        store.Save(new Profile { Name = "same name", Model = "/tmp/one.gguf" });

        Assert.Throws<IOException>(() => store.Save(new Profile { Name = "same-name", Model = "/tmp/two.gguf" }));
        Assert.Single(store.LoadAll().Profiles);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
