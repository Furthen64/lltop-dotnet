using System.Globalization;
using System.Text;

sealed class Profile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string LlamaServer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public string Alias { get; set; } = "";
    public int Ctx { get; set; } = 65536;
    public int Ngl { get; set; } = 99;
    public string CacheK { get; set; } = "q4_0";
    public string CacheV { get; set; } = "q4_0";
    public double Temp { get; set; } = .1;
    public double TopP { get; set; } = .95;
    public int TopK { get; set; } = 40;
    public double MinP { get; set; } = .05;
    public int Batch { get; set; } = 512;
    public int UBatch { get; set; } = 256;
    public int Parallel { get; set; } = 1;
    public int Threads { get; set; }
    public string FlashAttn { get; set; } = "auto";
    public bool Jinja { get; set; } = true;
    public bool Metrics { get; set; } = true;
    public bool NoMmap { get; set; } = true;
    public string ChatTemplate { get; set; } = "chatml";
    public string Reasoning { get; set; } = "auto";
    public int ReasoningBudget { get; set; } = -1;
    public List<string> ExtraArgs { get; set; } = [];
    public string SourcePath { get; set; } = "";

    public static Profile CreateDefault(AppConfig cfg, string name) => new()
    {
        Name = name,
        LlamaServer = cfg.LlamaServer,
        Host = cfg.DefaultHost,
        Port = cfg.DefaultPort
    };

    public Profile Copy(string name) => new()
    {
        Name = name, Description = Description, LlamaServer = LlamaServer, Model = Model,
        Host = Host, Port = Port, Alias = Alias, Ctx = Ctx, Ngl = Ngl,
        CacheK = CacheK, CacheV = CacheV, Temp = Temp, TopP = TopP, TopK = TopK,
        MinP = MinP, Batch = Batch, UBatch = UBatch, Parallel = Parallel, Threads = Threads,
        FlashAttn = FlashAttn, Jinja = Jinja, Metrics = Metrics, NoMmap = NoMmap,
        ChatTemplate = ChatTemplate, Reasoning = Reasoning, ReasoningBudget = ReasoningBudget,
        ExtraArgs = [.. ExtraArgs], SourcePath = SourcePath
    };

    public void Validate(bool forLaunch = false, AppConfig? cfg = null)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("Profile name is required.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
        if (!new[] { "", "auto", "on", "off" }.Contains(FlashAttn, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Flash attention must be auto, on, or off.");
        if (!new[] { "", "auto", "on", "off" }.Contains(Reasoning, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reasoning must be auto, on, or off.");
        if (ReasoningBudget < -1) throw new InvalidOperationException("Reasoning budget must be -1 or greater.");
        if (!forLaunch) return;
        var server = string.IsNullOrWhiteSpace(LlamaServer) ? cfg?.LlamaServer : LlamaServer;
        if (string.IsNullOrWhiteSpace(server)) throw new InvalidOperationException("llama-server path is required.");
        if (!File.Exists(server)) throw new FileNotFoundException("llama-server was not found.", server);
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Model path is required.");
        if (!File.Exists(Model)) throw new FileNotFoundException("Model was not found.", Model);
        if (Ctx <= 0 || Batch <= 0 || UBatch <= 0 || Parallel <= 0)
            throw new InvalidOperationException("Context, batch, micro-batch, and parallel values must be greater than zero.");
        if (Ngl < 0) throw new InvalidOperationException("GPU layers cannot be negative.");
    }
}

sealed record ProfileLoadResult(List<Profile> Profiles, List<string> Errors);

sealed class ProfileStore(string directory)
{
    public ProfileLoadResult LoadAll()
    {
        Directory.CreateDirectory(directory);
        var profiles = new List<Profile>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.toml").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profile = new Profile();
                Toml.ReadInto(path, profile);
                if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = Path.GetFileNameWithoutExtension(path);
                profile.Model = AppConfig.Expand(profile.Model);
                profile.LlamaServer = AppConfig.Expand(profile.LlamaServer);
                profile.SourcePath = path;
                profile.Validate();
                profiles.Add(profile);
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        return new(profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), errors);
    }

    public void Save(Profile profile)
    {
        profile.Validate();
        Directory.CreateDirectory(directory);
        var oldPath = profile.SourcePath;
        var path = Path.Combine(directory, Slugify(profile.Name) + ".toml");
        if (string.IsNullOrEmpty(oldPath) && File.Exists(path))
            throw new IOException($"A profile file named {Path.GetFileName(path)} already exists.");
        if (!string.IsNullOrEmpty(oldPath) && !Path.GetFullPath(oldPath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            throw new IOException($"A profile file named {Path.GetFileName(path)} already exists.");
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(profile));
        File.Move(temp, path, true);
        if (!string.IsNullOrEmpty(oldPath) && !Path.GetFullPath(oldPath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) File.Delete(oldPath);
        profile.SourcePath = path;
    }

    public void Delete(Profile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.SourcePath)) throw new InvalidOperationException("Profile has no source file.");
        File.Delete(profile.SourcePath);
    }

    public string UniqueName(string requested)
    {
        var loaded = LoadAll().Profiles;
        if (!loaded.Any(p => p.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))) return requested;
        for (var i = 2; ; i++)
        {
            var candidate = $"{requested}-{i}";
            if (!loaded.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    public static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "profile" : slug;
    }

    static string Serialize(Profile p)
    {
        var b = new StringBuilder();
        void S(string key, string value) => b.Append(key).Append(" = ").AppendLine(Toml.Quote(value));
        void I(string key, int value) => b.Append(key).Append(" = ").AppendLine(value.ToString(CultureInfo.InvariantCulture));
        void D(string key, double value) => b.Append(key).Append(" = ").AppendLine(value.ToString("0.###", CultureInfo.InvariantCulture));
        void B(string key, bool value) => b.Append(key).Append(" = ").AppendLine(value ? "true" : "false");
        S("name", p.Name); S("description", p.Description);
        if (!string.IsNullOrWhiteSpace(p.LlamaServer)) S("llama_server", p.LlamaServer);
        S("model", p.Model); S("host", p.Host); I("port", p.Port); S("alias", p.Alias);
        I("ctx", p.Ctx); I("ngl", p.Ngl); S("cache_k", p.CacheK); S("cache_v", p.CacheV);
        D("temp", p.Temp); D("top_p", p.TopP); I("top_k", p.TopK); D("min_p", p.MinP);
        I("batch", p.Batch); I("ubatch", p.UBatch); I("parallel", p.Parallel); I("threads", p.Threads);
        S("flash_attn", p.FlashAttn); B("jinja", p.Jinja); B("metrics", p.Metrics); B("no_mmap", p.NoMmap);
        S("chat_template", p.ChatTemplate); S("reasoning", p.Reasoning); I("reasoning_budget", p.ReasoningBudget);
        b.Append("extra_args = [").Append(string.Join(", ", p.ExtraArgs.Select(Toml.Quote))).AppendLine("]");
        return b.ToString();
    }
}

static class Toml
{
    public static void ReadInto<T>(string path, T target)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0 || line.StartsWith('[')) continue;
            var equals = line.IndexOf('=');
            if (equals < 1) throw new FormatException($"Invalid TOML line: {raw.Trim()}");
            var key = Normalize(line[..equals]);
            var value = line[(equals + 1)..].Trim();
            var prop = typeof(T).GetProperties().FirstOrDefault(p => Normalize(p.Name) == key);
            if (prop is null || !prop.CanWrite) continue;
            try
            {
                object parsed = prop.PropertyType == typeof(string) ? Unquote(value)
                    : prop.PropertyType == typeof(int) ? int.Parse(value, CultureInfo.InvariantCulture)
                    : prop.PropertyType == typeof(double) ? double.Parse(value, CultureInfo.InvariantCulture)
                    : prop.PropertyType == typeof(bool) ? bool.Parse(value)
                    : prop.PropertyType == typeof(List<string>) ? ParseArray(value)
                    : throw new NotSupportedException();
                prop.SetValue(target, parsed);
            }
            catch (Exception ex) { throw new FormatException($"Invalid value for '{line[..equals].Trim()}': {ex.Message}"); }
        }
    }

    static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            if (quote == '\0' && line[i] is '\'' or '"') quote = line[i];
            else if (line[i] == quote && (i == 0 || line[i - 1] != '\\')) quote = '\0';
            else if (line[i] == '#' && quote == '\0') return line[..i];
        }
        return line;
    }
    static List<string> ParseArray(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('[') || !value.EndsWith(']')) throw new FormatException("Expected an array.");
        return value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote).ToList();
    }
    static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"') value = value[1..^1];
        return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
    public static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
