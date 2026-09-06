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
        Write("imatrix_unsloth.gguf");
        WriteGgufWithType("calibration.gguf", "imatrix");
        Write("ignore.txt");

        var models = FirstRunProfiles.DiscoverModels(root);

        Assert.Single(models);
        Assert.Contains(Path.Combine(root, "top.gguf"), models);
        Assert.DoesNotContain(Path.Combine(root, "imatrix_unsloth.gguf"), models);
        Assert.DoesNotContain(Path.Combine(root, "calibration.gguf"), models);
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

    [Fact]
    public void InspectModels_ReportsAndSkipsEmptyOrInvalidGgufs()
    {
        Write("good.gguf");
        Write("empty.gguf", "");
        Write("broken.gguf", "not a GGUF");

        var models = FirstRunProfiles.InspectModels(root);

        Assert.Collection(models,
            model => { Assert.Equal(Path.Combine(root, "broken.gguf"), model.Path); Assert.False(model.IsRunnable); Assert.StartsWith("Invalid GGUF:", model.Status); },
            model => { Assert.Equal(Path.Combine(root, "empty.gguf"), model.Path); Assert.False(model.IsRunnable); Assert.Equal("Empty file", model.Status); },
            model => { Assert.Equal(Path.Combine(root, "good.gguf"), model.Path); Assert.True(model.IsRunnable); Assert.StartsWith("GGUF verified", model.Status); });
        Assert.Equal([Path.Combine(root, "good.gguf")], FirstRunProfiles.DiscoverModels(root));
        Assert.Equal(1, FirstRunProfiles.ScanAndGenerate(Config()).ProfilesCreated);
        Assert.Single(new ProfileStore(Config().ProfilesDir).LoadAll().Profiles);
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

    void Write(string relativePath, string? contents = null)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (contents is not null) { File.WriteAllText(path, contents); return; }
        if (Path.GetExtension(path).Equals(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write("GGUF".Select(character => (byte)character).ToArray());
            writer.Write((uint)3);
            writer.Write((ulong)0); // tensor count is not needed for metadata inspection
            writer.Write((ulong)0); // metadata count
            return;
        }
        File.WriteAllText(path, "model");
    }

    void WriteGgufWithType(string relativePath, string type)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("GGUF".Select(character => (byte)character).ToArray());
        writer.Write((uint)3);
        writer.Write((ulong)0);
        writer.Write((ulong)1);
        WriteString("general.type");
        writer.Write((uint)8); // GGUF string
        WriteString(type);

        void WriteString(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
