sealed record GeneratedProfilesResult(int ModelsFound, int ProfilesCreated);

static class FirstRunProfiles
{
    internal const int ModelSearchDepth = 3;

    public static IReadOnlyList<string> DiscoverModels(string modelsDirectory, int maxDepth = ModelSearchDepth)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory)) return [];
        if (maxDepth < 1) return [];

        var root = Path.GetFullPath(modelsDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Models directory was not found: {root}");

        var models = new List<string>();
        Scan(root, 1);
        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;

        void Scan(string directory, int fileDepth)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    var extension = Path.GetExtension(path);
                    if (extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                        models.Add(Path.GetFullPath(path));
                }

                if (fileDepth >= maxDepth) return;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Scan(child, fileDepth + 1);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static GeneratedProfilesResult Generate(AppConfig cfg, IEnumerable<string> modelPaths)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(modelPaths);

        Directory.CreateDirectory(cfg.ProfilesDir);
        var store = new ProfileStore(cfg.ProfilesDir);
        EnsureStarterProfile(cfg, store);

        var existingSlugs = Directory.EnumerateFiles(cfg.ProfilesDir, "*.toml")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var profiledModels = store.LoadAll().Profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Model))
            .Select(profile => Path.GetFullPath(profile.Model))
            .ToHashSet(pathComparer);
        var models = modelPaths.Select(Path.GetFullPath).ToList();
        var created = 0;

        foreach (var modelPath in models)
        {
            if (!profiledModels.Add(modelPath)) continue;

            var baseSlug = ProfileStore.Slugify(Path.GetFileNameWithoutExtension(modelPath));
            var slug = UniqueSlug(baseSlug, existingSlugs);
            existingSlugs.Add(slug);

            var profile = CreateForModel(cfg, slug, modelPath);
            store.Save(profile);
            created++;
        }

        return new(models.Count, created);
    }

    public static GeneratedProfilesResult ScanAndGenerate(AppConfig cfg)
    {
        var models = DiscoverModels(cfg.ModelsDir);
        return Generate(cfg, models);
    }

    public static Profile CreateForModel(AppConfig cfg, string name, string modelPath)
    {
        var profile = Profile.CreateDefault(cfg, name);
        profile.Model = Path.GetFullPath(modelPath);
        profile.Description = "Auto-generated from model discovery";

        var modelName = Path.GetFileNameWithoutExtension(modelPath);
        if (modelName.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) ApplyDeepSeek(profile, modelName);
        else if (IsGptOss(modelName)) ApplyGptOss(profile);
        else if (modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase)) ApplyQwen(profile);
        else ApplyUnknown(profile);

        return profile;
    }

    static void EnsureStarterProfile(AppConfig cfg, ProfileStore store)
    {
        var path = Path.Combine(cfg.ProfilesDir, "starter.toml");
        if (File.Exists(path)) return;
        var starter = Profile.CreateDefault(cfg, "starter");
        starter.Description = "Starter profile";
        store.Save(starter);
    }

    static string UniqueSlug(string baseSlug, HashSet<string> existing)
    {
        if (!existing.Contains(baseSlug)) return baseSlug;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseSlug}-{suffix}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    static bool IsGptOss(string modelName)
    {
        var compact = new string(modelName.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("gptoss", StringComparison.OrdinalIgnoreCase);
    }

    static void ApplyQwen(Profile profile)
    {
        profile.Alias = "qwen";
        profile.Ctx = 65536;
        profile.Ngl = 99;
        profile.CacheK = "q4_0";
        profile.CacheV = "q4_0";
        profile.Temp = .6;
        profile.TopP = .95;
        profile.TopK = 20;
        profile.MinP = 0;
        profile.ChatTemplate = "chatml";
        profile.Reasoning = "auto";
    }

    static void ApplyGptOss(Profile profile)
    {
        profile.Alias = "gpt-oss";
        profile.Ctx = 131072;
        profile.Ngl = 99;
        profile.CacheK = "q8_0";
        profile.CacheV = "q8_0";
        profile.Temp = 1;
        profile.TopP = 1;
        profile.TopK = 0;
        profile.MinP = .05;
        profile.ChatTemplate = "";
        profile.Reasoning = "auto";
    }

    static void ApplyDeepSeek(Profile profile, string modelName)
    {
        profile.Alias = "deepseek";
        profile.Ctx = 65536;
        profile.Ngl = 99;
        profile.CacheK = "q4_0";
        profile.CacheV = "q4_0";
        profile.Temp = .6;
        profile.TopP = .95;
        profile.TopK = 20;
        profile.MinP = 0;
        profile.ChatTemplate = modelName.Contains("v3", StringComparison.OrdinalIgnoreCase) ? "deepseek3"
            : modelName.Contains("v2", StringComparison.OrdinalIgnoreCase) ? "deepseek2"
            : "deepseek";
        profile.Reasoning = "auto";
    }

    static void ApplyUnknown(Profile profile)
    {
        profile.Alias = "";
        profile.Ctx = 4096;
        profile.Ngl = 0;
        profile.CacheK = "";
        profile.CacheV = "";
        profile.Temp = .8;
        profile.TopP = .95;
        profile.TopK = 40;
        profile.MinP = .05;
        profile.FlashAttn = "auto";
        profile.Jinja = false;
        profile.NoMmap = false;
        profile.ChatTemplate = "";
        profile.Reasoning = "auto";
    }
}
