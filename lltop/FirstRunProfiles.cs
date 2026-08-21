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
        var ignore = ModelIgnore.Load(root);

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
                    if (ignore.IsIgnored(Path.GetRelativePath(root, path), isDirectory: false)) continue;
                    if (Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
                    var extension = Path.GetExtension(path);
                    if (extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var meta = GgufMetadataReader.Read(path);
                            var arch = meta.String("general.architecture");
                            if (!string.IsNullOrEmpty(arch) && !IsNonChatArchitecture(arch))
                                models.Add(Path.GetFullPath(path));
                        }
                        catch { /* skip files we can't read */ }
                    }
                }

                if (fileDepth >= maxDepth) return;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if (ignore.IsIgnored(Path.GetRelativePath(root, child), isDirectory: true)) continue;
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

    public static GeneratedProfilesResult Generate(AppConfig cfg, IEnumerable<string> modelPaths, ServerCapabilityRecord? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(modelPaths);

        Directory.CreateDirectory(cfg.ProfilesDir);
        var store = new ProfileStore(cfg.ProfilesDir);
        RemoveLegacyStarter(cfg);

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

            var profile = CreateForModel(cfg, slug, modelPath, capabilities);
            store.Save(profile);
            created++;
        }

        return new(models.Count, created);
    }

    public static GeneratedProfilesResult ScanAndGenerate(AppConfig cfg, ServerCapabilityRecord? capabilities = null)
    {
        var models = DiscoverModels(cfg.ModelsDir);
        return Generate(cfg, models, capabilities);
    }

    public static Profile CreateForModel(AppConfig cfg, string name, string modelPath, ServerCapabilityRecord? capabilities = null)
    {
        var profile = Profile.CreateDefault(cfg, name);
        profile.Model = Path.GetFullPath(modelPath);
        profile.Description = "Auto-generated from model discovery";

        var modelName = Path.GetFileNameWithoutExtension(modelPath);
        if (modelName.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) ApplyDeepSeek(profile, modelName);
        else if (IsGptOss(modelName)) ApplyGptOss(profile);
        else if (modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase)) ApplyQwen(profile, modelName);
        else ApplyUnknown(profile);
        ApplyHardwareDefaults(profile, capabilities);
        ApplyCapabilityDefaults(profile, capabilities);

        return profile;
    }

    public static bool RemoveLegacyStarter(AppConfig cfg)
    {
        var path = Path.Combine(cfg.ProfilesDir, "starter.toml");
        if (!File.Exists(path)) return false;
        try
        {
            var starter = new Profile();
            Toml.ReadInto(path, starter);
            if (!starter.Name.Equals("starter", StringComparison.OrdinalIgnoreCase) ||
                !starter.Description.Equals("Starter profile", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(starter.Model)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
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

    static void ApplyQwen(Profile profile, string modelName)
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
        // Let llama.cpp use the model's embedded template, which carries model-specific
        // tokens and multimodal handling that a generic ChatML override would discard.
        profile.ChatTemplate = "";
        profile.Reasoning = "auto";
        if (modelName.Contains("qwen3.8", StringComparison.OrdinalIgnoreCase)) profile.ImageMinTokens = 1024;
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

    static void ApplyHardwareDefaults(Profile profile, ServerCapabilityRecord? capabilities)
    {
        if (capabilities?.IsPascalCuda != true) return;
        profile.Ctx = 4096;
        profile.Batch = 256;
        profile.UBatch = 128;
        profile.Parallel = 1;
        profile.FlashAttn = "off";
    }

    static void ApplyCapabilityDefaults(Profile profile, ServerCapabilityRecord? capabilities)
    {
        if (capabilities is null) return;
        if (!capabilities.SupportsOption("--reasoning")) profile.Reasoning = "";
        if (!capabilities.SupportsOption("--reasoning-budget")) profile.ReasoningBudget = -1;
        if (!capabilities.SupportsOption("--chat-template")) profile.ChatTemplate = "";
        if (!capabilities.SupportsOption("--jinja")) profile.Jinja = false;
        if (!capabilities.SupportsOption("--metrics")) profile.Metrics = false;
        if (!capabilities.SupportsOption("--no-mmap")) profile.NoMmap = false;
        if (!capabilities.SupportsOption("--flash-attn")) profile.FlashAttn = "";
        if (!capabilities.SupportsOption("--cache-type-k")) profile.CacheK = "";
        if (!capabilities.SupportsOption("--cache-type-v")) profile.CacheV = "";
    }

    static bool IsNonChatArchitecture(string arch)
    {
        var a = arch.ToLowerInvariant();
        return a == "bert" || a == "nomic-bert" || a == "jina" || a == "e5-mistral" ||
               a == "colbert-architectures" || a == "jina-colbert-v2";
    }
}
