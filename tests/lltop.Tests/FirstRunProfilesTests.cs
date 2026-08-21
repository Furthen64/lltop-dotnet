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
        Write("mmproj-BF16.gguf");
        Write("ignore.txt");

        var models = FirstRunProfiles.DiscoverModels(root);

        Assert.Equal(2, models.Count);
        Assert.Contains(Path.Combine(root, "top.gguf"), models);
        Assert.Contains(Path.Combine(root, "one/two/model.BIN"), models);
    }

    [Fact]
    public void DiscoverModels_RespectsRootLlmIgnorePatterns()
    {
        Write("keep.gguf");
        Write("archive/old.gguf");
        Write("experiments/drop.gguf");
        Write("experiments/keep.gguf");
        Write("scratch/drop.gguf");
        Write("scratch/nested/drop.gguf");
        File.WriteAllText(Path.Combine(root, ".llmignore"), "# local exclusions\narchive/\nexperiments/*.gguf\n!experiments/keep.gguf\nscratch/**/*.gguf\n");

        var models = FirstRunProfiles.DiscoverModels(root);

        Assert.Equal([Path.Combine(root, "experiments/keep.gguf"), Path.Combine(root, "keep.gguf")], models);
    }

    [Theory]
    [InlineData("Qwen3-Coder-30B.gguf", "qwen", "", 65536)]
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

    [Theory]
    [InlineData("Qwen3-Coder-30B.gguf")]
    [InlineData("DeepSeek-V3-Q4.gguf")]
    public void CreateForModel_UsesConservativeSamplingAndQ8Caches(string fileName)
    {
        var profile = FirstRunProfiles.CreateForModel(Config(), "generated", Path.Combine(root, fileName));

        Assert.Equal(.1, profile.Temp);
        Assert.Equal("q8_0", profile.CacheK);
        Assert.Equal("q8_0", profile.CacheV);
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
    public void CreateForModel_AppliesQwen38VisionRecommendation()
    {
        var profile = FirstRunProfiles.CreateForModel(Config(), "qwen38", Path.Combine(root, "Qwen3.8-27B-IQ3.gguf"));

        Assert.Empty(profile.ChatTemplate);
        Assert.Equal(1024, profile.ImageMinTokens);
    }

    [Fact]
    public void Generate_CreatesUniqueProfilesWithoutAnEmptyStarter()
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
        Assert.DoesNotContain(loaded.Profiles, profile => profile.Name == "starter");
        Assert.Contains(loaded.Profiles, profile => profile.Name == "qwen3-2" && profile.Alias == "qwen");
        Assert.Contains(loaded.Profiles, profile => profile.Name == "other" && profile.Ctx == 4096);
    }

    [Fact]
    public void ScanAndGenerate_OnRefreshCreatesOnlyProfilesForNewModels()
    {
        var cfg = Config();
        Write("Qwen3.gguf");

        var first = FirstRunProfiles.ScanAndGenerate(cfg);
        Write("family/releases/DeepSeek-V3.gguf");
        var refresh = FirstRunProfiles.ScanAndGenerate(cfg);
        var loaded = new ProfileStore(cfg.ProfilesDir).LoadAll();

        Assert.Equal(1, first.ProfilesCreated);
        Assert.Equal(2, refresh.ModelsFound);
        Assert.Equal(1, refresh.ProfilesCreated);
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.Contains(loaded.Profiles, profile => profile.Name == "qwen3" && profile.ChatTemplate == "");
        Assert.Contains(loaded.Profiles, profile => profile.Name == "deepseek-v3" && profile.ChatTemplate == "deepseek3");
    }

    [Fact]
    public void RemoveLegacyStarter_DeletesOnlyTheOldEmptyGeneratedProfile()
    {
        var cfg = Config();
        Directory.CreateDirectory(cfg.ProfilesDir);
        var starter = Profile.CreateDefault(cfg, "starter");
        starter.Description = "Starter profile";
        new ProfileStore(cfg.ProfilesDir).Save(starter);

        Assert.True(FirstRunProfiles.RemoveLegacyStarter(cfg));
        Assert.False(File.Exists(Path.Combine(cfg.ProfilesDir, "starter.toml")));
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
