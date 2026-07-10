using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record RunIssue(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("seen_at_seconds")] double SeenAtSeconds);

internal sealed class RunRecord
{
    [JsonPropertyName("run_id")] public string RunId { get; set; } = "";
    [JsonPropertyName("profile_name")] public string ProfileName { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; set; }
    [JsonPropertyName("ended_at")] public DateTimeOffset EndedAt { get; set; }
    [JsonPropertyName("duration_seconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("exit_code")] public int ExitCode { get; set; }
    [JsonPropertyName("exit_reason")] public string ExitReason { get; set; } = "";
    [JsonPropertyName("generated_command")] public string GeneratedCommand { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("ctx")] public int Ctx { get; set; }
    [JsonPropertyName("ngl")] public int Ngl { get; set; }
    [JsonPropertyName("cache_k")] public string CacheK { get; set; } = "";
    [JsonPropertyName("cache_v")] public string CacheV { get; set; } = "";
    [JsonPropertyName("batch")] public int Batch { get; set; }
    [JsonPropertyName("ubatch")] public int UBatch { get; set; }
    [JsonPropertyName("parallel")] public int Parallel { get; set; }
    [JsonPropertyName("reasoning")] public string Reasoning { get; set; } = "";
    [JsonPropertyName("reasoning_budget")] public int ReasoningBudget { get; set; }
    [JsonPropertyName("no_mmap")] public bool NoMmap { get; set; }
    [JsonPropertyName("extra_args")] public List<string> ExtraArgs { get; set; } = [];
    [JsonPropertyName("last_prompt_tokens_per_second")] public double PromptTokensPerSecond { get; set; }
    [JsonPropertyName("last_eval_tokens_per_second")] public double EvalTokensPerSecond { get; set; }
    [JsonPropertyName("last_generated_tokens")] public int GeneratedTokens { get; set; }
    [JsonPropertyName("last_prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("offloaded_layers")] public int OffloadedLayers { get; set; }
    [JsonPropertyName("total_layers")] public int TotalLayers { get; set; }
    [JsonPropertyName("gpu_total_mib")] public int GpuTotalMiB { get; set; }
    [JsonPropertyName("gpu_free_mib")] public int GpuFreeMiB { get; set; }
    [JsonPropertyName("gpu_model_mib")] public int GpuModelMiB { get; set; }
    [JsonPropertyName("gpu_context_mib")] public int GpuContextMiB { get; set; }
    [JsonPropertyName("gpu_compute_mib")] public int GpuComputeMiB { get; set; }
    [JsonPropertyName("issues")] public List<RunIssue> Issues { get; set; } = [];

    public static RunRecord Create(Profile p, string command, DateTimeOffset started, DateTimeOffset ended, int exitCode, string reason, ServerStats stats) => new()
    {
        RunId = $"{started:yyyyMMdd_HHmmss}_{ProfileStore.Slugify(p.Name)}", ProfileName = p.Name,
        StartedAt = started, EndedAt = ended, DurationSeconds = Math.Max(0, (ended - started).TotalSeconds),
        ExitCode = exitCode, ExitReason = reason, GeneratedCommand = command,
        Model = p.Model, Ctx = p.Ctx, Ngl = p.Ngl, CacheK = p.CacheK, CacheV = p.CacheV, Batch = p.Batch, UBatch = p.UBatch,
        Parallel = p.Parallel, Reasoning = p.Reasoning, ReasoningBudget = p.ReasoningBudget, NoMmap = p.NoMmap, ExtraArgs = [.. p.ExtraArgs],
        PromptTokensPerSecond = stats.PromptTokensPerSecond, EvalTokensPerSecond = stats.EvalTokensPerSecond,
        GeneratedTokens = stats.GeneratedTokens, PromptTokens = stats.PromptTokens,
        OffloadedLayers = stats.OffloadedLayers, TotalLayers = stats.TotalLayers,
        GpuTotalMiB = stats.GpuTotalMiB, GpuFreeMiB = stats.GpuFreeMiB, GpuModelMiB = stats.GpuModelMiB,
        GpuContextMiB = stats.GpuContextMiB, GpuComputeMiB = stats.GpuComputeMiB, Issues = [.. stats.Issues]
    };
}

internal sealed record RunRecordRef(string Path, RunRecord Record);
internal sealed record MetricSummary(int Count, double Latest, double Average, double Median, double Min, double Max, IReadOnlyList<double> Series);
internal sealed record ProfileRunSummary(string ProfileName, int RunCount, MetricSummary Prompt, MetricSummary Generation);

internal static class RunHistory
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string Save(string directory, RunRecord record)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{record.StartedAt:yyyy-MM-dd_HHmmss}_{ProfileStore.Slugify(record.ProfileName)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, Json) + Environment.NewLine);
        return path;
    }

    public static List<RunRecordRef> Load(string directory) => !Directory.Exists(directory) ? [] : Directory.EnumerateFiles(directory, "*.json")
        .OrderByDescending(x => x, StringComparer.Ordinal).Select(path => new RunRecordRef(path, JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"Invalid run record: {path}"))).ToList();

    public static void Update(string path, RunRecord record) => File.WriteAllText(path, JsonSerializer.Serialize(record, Json) + Environment.NewLine);

    public static List<RunRecordRef> ForProfile(string directory, string profile) => Load(directory).Where(x => x.Record.ProfileName.Equals(profile, StringComparison.OrdinalIgnoreCase)).ToList();

    public static ProfileRunSummary Summarize(string directory, string profile)
    {
        var records = ForProfile(directory, profile).Select(x => x.Record).OrderBy(x => x.StartedAt).ToList();
        return new(profile, records.Count, Summary(records.Select(x => x.PromptTokensPerSecond)), Summary(records.Select(x => x.EvalTokensPerSecond)));
    }

    static MetricSummary Summary(IEnumerable<double> source)
    {
        var values = source.Where(x => x > 0).ToList();
        if (values.Count == 0) return new(0, 0, 0, 0, 0, 0, []);
        var sorted = values.Order().ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return new(values.Count, values[^1], values.Average(), median, sorted[0], sorted[^1], values);
    }

    public static string Sparkline(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return "";
        const string levels = "▁▂▃▄▅▆▇█";
        var min = values.Min(); var max = values.Max();
        if (max <= min) return new string(levels[levels.Length / 2], values.Count);
        return string.Concat(values.Select(x => levels[(int)Math.Round((x - min) / (max - min) * (levels.Length - 1))]));
    }

    public static RunRecord? FindRecentFailure(string directory, Profile p, int windowSeconds, int startupSeconds)
    {
        var cutoff = DateTimeOffset.Now.AddSeconds(-windowSeconds);
        return Load(directory).Select(x => x.Record).Where(x => x.StartedAt >= cutoff && x.ExitCode != 0 && x.DurationSeconds < startupSeconds && SameScenario(x, p)).OrderByDescending(x => x.StartedAt).FirstOrDefault();
    }

    static bool SameScenario(RunRecord s, Profile p) => s.Model == p.Model && s.Ctx == p.Ctx && s.Ngl == p.Ngl && s.CacheK == p.CacheK && s.CacheV == p.CacheV && s.Batch == p.Batch && s.UBatch == p.UBatch && s.Parallel == p.Parallel && s.Reasoning == p.Reasoning && s.ReasoningBudget == p.ReasoningBudget && s.NoMmap == p.NoMmap && (s.ExtraArgs ?? []).SequenceEqual(p.ExtraArgs);
}
