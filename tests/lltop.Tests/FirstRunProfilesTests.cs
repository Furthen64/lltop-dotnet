using Xunit;

public sealed class FirstRunProfilesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "lltop-first-run-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoverModels_FindsSupportedFilesThroughThreeLevels()
    {
        Write("top.gguf");
        Write("one/two/model.BIN");
        Write("one/two/three/too-deep.gguf");
        Write("ignore.txt");

        var models = FirstRunProfiles.DiscoverModels(root);

        Assert.Equal(2, models.Count);
        Assert.Contains(Path.Combine(root, "top.gguf"), models);
        Assert.Contains(Path.Combine(root, "one/two/model.BIN"), models);
    }

    [Theory]
    [InlineData("Qwen3-Coder-30B.gguf", "qwen", "chatml", 65536)]
    [InlineData("gpt-oss-20b-Q4.gguf", "gpt-oss", "", 131072)]
    [InlineData("GPTOSS-120B.gguf", "gpt-oss", "", 131072)]
    [InlineData("DeepSeek-R1-Distill-Qwen.gguf", "deepseek", "deepseek", 65536)]
    [InlineData("DeepSeek-V2-Lite.gguf", "deepseek", "deepseek2", 65536)]
    [InlineData("DeepSeek-V3-Q4.gguf", "deepseek", "deepseek3", 65536)]
    public void CreateForModel_SelectsFamilyTemplate(string fileName, string alias, string chatTemplate, int context)
    {
        var profile = FirstRunProfiles.CreateForModel(Config(), "generated", Path.Combine(root, fileName));

        Assert.Equal(alias, profile.Alias);
        Assert.Equal(chatTemplate, profile.ChatTemplate);
        Assert.Equal(context, profile.Ctx);
    }

    [Fact]
    public void CreateForModel_UsesSimpleUnknownDefaults()
    {
        var profile = FirstRunProfiles.CreateForModel(Config(), "mystery", Path.Combine(root, "Mystery-7B.gguf"));

        Assert.Equal(4096, profile.Ctx);
        Assert.Equal(0, profile.Ngl);
        Assert.Empty(profile.ChatTemplate);
        Assert.Empty(profile.CacheK);
        Assert.False(profile.Jinja);
        Assert.False(profile.NoMmap);
    }

    [Fact]
    public void Generate_CreatesStarterAndUniqueProfilesWithSelectedDefaults()
    {
        var cfg = Config();
        Directory.CreateDirectory(cfg.ProfilesDir);
        new ProfileStore(cfg.ProfilesDir).Save(new Profile { Name = "qwen3", Model = "/existing.gguf" });

        var result = FirstRunProfiles.Generate(cfg,
        [
            Path.Combine(root, "Qwen3.gguf"),
            Path.Combine(root, "other.gguf")
        ]);
        var loaded = new ProfileStore(cfg.ProfilesDir).LoadAll();

        Assert.Equal(2, result.ModelsFound);
        Assert.Equal(2, result.ProfilesCreated);
        Assert.Empty(loaded.Errors);
        Assert.Contains(loaded.Profiles, profile => profile.Name == "starter");
        Assert.Contains(loaded.Profiles, profile => profile.Name == "qwen3-2" && profile.Alias == "qwen");
        Assert.Contains(loaded.Profiles, profile => profile.Name == "other" && profile.Ctx == 4096);
    }

    AppConfig Config() => new()
    {
        LlamaServer = "/llama-server",
        ModelsDir = root,
        ProfilesDir = Path.Combine(root, "profiles")
    };

    void Write(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "model");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
