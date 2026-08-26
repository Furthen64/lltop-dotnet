using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record LlamaRuntimeEvent(string Event, IReadOnlyDictionary<string, object?> Fields, string Raw);

// Isolated from process/UI code because llama.cpp's human-facing log format is
// not a stable API. Unknown and malformed lines simply produce no event.
internal static partial class LlamaRuntimeEventParser
{
    internal static LlamaRuntimeEvent? Parse(string line)
    {
        var fields = SlotAndTask(line);
        LlamaRuntimeEvent Make(string name, params (string Key, object? Value)[] values)
        {
            var all = new Dictionary<string, object?>(fields);
            foreach (var (key, value) in values) if (value is not null) all[key] = value;
            return new(name, all, line);
        }

        var basic = LlamaLogParser.Parse(line);
        if (basic.IsError) return Make("error", ("error_kind", basic.ErrorKind), ("message", basic.ErrorMessage));
        if (basic.Cancelled) return Make("cancelled");
        if (line.Contains("server is listening", StringComparison.OrdinalIgnoreCase)) return Make("server_ready");
        var save = PromptSave().Match(line);
        if (save.Success) return Make("prompt_cache_save", ("prompt_tokens", Int(save, 1)), ("state_mib", Double(save, 2)), ("draft_mib", Double(save, 3)));
        var state = CacheState().Match(line);
        if (state.Success) return Make("prompt_cache_state", ("prompts", Int(state, 1)), ("cache_mib", Double(state, 2)), ("cache_limit_mib", Double(state, 3)), ("cache_tokens", Int(state, 4)), ("cache_estimated_tokens", Int(state, 5)));
        var evict = CacheEvict().Match(line);
        if (evict.Success) return Make("prompt_cache_evict", ("evicted_mib", Double(evict, 1)));
        if (line.Contains("updating prompt cache", StringComparison.OrdinalIgnoreCase)) return Make("prompt_cache_update");
        var lookup = CacheLookup().Match(line);
        if (lookup.Success) return Make("prompt_cache_lookup", ("f_keep", Double(lookup, 1)), ("similarity", Double(lookup, 2)));
        var reuse = ContextReuse().Match(line);
        if (reuse.Success) return Make("context_reuse", ("similarity", Double(reuse, 1)), ("f_keep", Double(reuse, 2)));
        var create = CheckpointCreate().Match(line);
        if (create.Success) return Make("checkpoint_create", ("checkpoint", Int(create, 1)), ("checkpoint_limit", Int(create, 2)), ("pos_min", Int(create, 3)), ("pos_max", Int(create, 4)), ("tokens", Int(create, 5)), ("size_mib", Double(create, 6)));
        var restore = CheckpointRestore().Match(line);
        if (restore.Success) return Make("checkpoint_restore", ("pos_min", Int(restore, 1)), ("pos_max", Int(restore, 2)), ("tokens", Int(restore, 3)), ("n_past", Int(restore, 4)), ("size_mib", Double(restore, 5)));
        var erase = CheckpointErase().Match(line);
        if (erase.Success) return Make("checkpoint_erase", ("pos_min", Int(erase, 1)), ("pos_max", Int(erase, 2)), ("tokens", Int(erase, 3)));
        if (line.Contains("forcing full prompt re-processing due to lack of cache data", StringComparison.OrdinalIgnoreCase)) return Make("full_prompt_reprocess");
        var start = RequestStart().Match(line);
        if (start.Success) return Make("request_start", ("is_child", Int(start, 1)));
        var end = RequestEnd().Match(line);
        if (end.Success) return Make("request_end", ("context_tokens", Int(end, 1)), ("truncated", Int(end, 2)));
        if (line.Contains("all slots are idle", StringComparison.OrdinalIgnoreCase)) return Make("slots_idle");
        var prompt = PromptEval().Match(line);
        if (prompt.Success) return Make("prompt_eval", ("prompt_eval_ms", Double(prompt, 1)), ("prompt_eval_tokens", Int(prompt, 2)), ("prompt_tps", Double(prompt, 3)));
        return null;
    }

    internal static IReadOnlyDictionary<string, object?>? ParseGenerationTelemetry(string line)
    {
        var generation = Generation().Match(line);
        return generation.Success ? new Dictionary<string, object?> { ["decoded_tokens"] = Int(generation, 1), ["generation_tps"] = Double(generation, 2), ["generation_tps_3s"] = Double(generation, 3) } : null;
    }

    static Dictionary<string, object?> SlotAndTask(string line)
    {
        var match = SlotTask().Match(line);
        var result = new Dictionary<string, object?>();
        if (match.Success) { result["slot"] = Int(match, 1); result["task"] = Int(match, 2); }
        return result;
    }
    static int? Int(Match match, int group) => int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    static double? Double(Match match, int group) => double.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    [GeneratedRegex(@"saving prompt with length\s+(\d+),\s+total state size\s*=\s*([\d.]+) MiB\s*\(draft:\s*([\d.]+) MiB\)", RegexOptions.IgnoreCase)] private static partial Regex PromptSave();
    [GeneratedRegex(@"cache state:\s*(\d+) prompts,\s*([\d.]+) MiB\s*\(limits:\s*([\d.]+) MiB,\s*(\d+) tokens,\s*(\d+) est", RegexOptions.IgnoreCase)] private static partial Regex CacheState();
    [GeneratedRegex(@"cache size limit reached, removing oldest entry \(size\s*=\s*([\d.]+) MiB\)", RegexOptions.IgnoreCase)] private static partial Regex CacheEvict();
    [GeneratedRegex(@"looking for better prompt, base f_keep\s*=\s*([\d.]+), sim\s*=\s*([\d.]+)", RegexOptions.IgnoreCase)] private static partial Regex CacheLookup();
    [GeneratedRegex(@"selected slot by LCP similarity, sim_best\s*=\s*([\d.]+).*?f_keep\s*=\s*([\d.]+)", RegexOptions.IgnoreCase)] private static partial Regex ContextReuse();
    [GeneratedRegex(@"created context checkpoint\s+(\d+) of (\d+) \(pos_min\s*=\s*(\d+), pos_max\s*=\s*(\d+), n_tokens\s*=\s*(\d+), size\s*=\s*([\d.]+) MiB\)", RegexOptions.IgnoreCase)] private static partial Regex CheckpointCreate();
    [GeneratedRegex(@"restored context checkpoint \(pos_min\s*=\s*(\d+), pos_max\s*=\s*(\d+), n_tokens\s*=\s*(\d+), n_past\s*=\s*(\d+), size\s*=\s*([\d.]+) MiB\)", RegexOptions.IgnoreCase)] private static partial Regex CheckpointRestore();
    [GeneratedRegex(@"erased invalidated context checkpoint \(pos_min\s*=\s*(\d+), pos_max\s*=\s*(\d+), n_tokens\s*=\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex CheckpointErase();
    [GeneratedRegex(@"processing task, is_child\s*=\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex RequestStart();
    [GeneratedRegex(@"stop processing: n_tokens\s*=\s*(\d+), truncated\s*=\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex RequestEnd();
    [GeneratedRegex(@"prompt eval time\s*=\s*([\d.]+) ms\s*/\s*(\d+) tokens(?:.*?,\s*([\d.]+) tokens per second)?", RegexOptions.IgnoreCase)] private static partial Regex PromptEval();
    [GeneratedRegex(@"n_decoded\s*=\s*(\d+),\s*tg\s*=\s*([\d.]+) t/s(?:,\s*tg_3s\s*=\s*([\d.]+) t/s)?", RegexOptions.IgnoreCase)] private static partial Regex Generation();
    [GeneratedRegex(@"(?:id|slot)\s*(\d+)\s*\|\s*task\s*(\d+)", RegexOptions.IgnoreCase)] private static partial Regex SlotTask();
}

internal sealed class RunGraphDataWriter : IDisposable
{
    readonly StreamWriter writer;
    readonly object gate = new();
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private RunGraphDataWriter(string path, StreamWriter writer) { Path = path; this.writer = writer; }
    public string Path { get; }

    public static RunGraphDataWriter Create(string directory, Profile profile, DateTimeOffset started)
    {
        Directory.CreateDirectory(directory);
        var path = UniquePath(directory, $"run-{started:yyyyMMdd-HHmmss}-{ProfileStore.Slugify(profile.Name)}");
        var result = new RunGraphDataWriter(path, new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true });
        result.WriteLine("# lltop realtime graph data v2"); result.WriteLine("# profile: " + Clean(profile.Name)); result.WriteLine("# started_at: " + started.ToString("O", CultureInfo.InvariantCulture));
        result.WriteLine("timestamp_utc\ttype\tcpu_percent\tsystem_ram_used_bytes\tsystem_ram_available_bytes\tsystem_ram_total_bytes\tswap_used_bytes\tswap_free_bytes\tllama_rss_bytes\tllama_pss_bytes\tllama_private_dirty_bytes\tllama_anonymous_bytes\tllama_swap_bytes\tgpu_percent\tvram_used_bytes\tvram_total_bytes\tevent\tevent_fields_json\traw");
        result.WriteEvent(started, new LlamaRuntimeEvent("run_started", new Dictionary<string, object?>(), "")); return result;
    }

    public void WriteSample(RunResourceSample sample) => WriteLine(string.Join('\t', Timestamp(sample.Timestamp), "sample", Number(sample.CpuUsagePercent), Number(sample.SystemRamUsedBytes), Number(sample.SystemRamAvailableBytes), Number(sample.SystemRamTotalBytes), Number(sample.SwapUsedBytes), Number(sample.SwapFreeBytes), Number(sample.LlamaRssBytes), Number(sample.LlamaPssBytes), Number(sample.LlamaPrivateDirtyBytes), Number(sample.LlamaAnonymousBytes), Number(sample.LlamaSwapBytes), Number(sample.GpuUsagePercent), Number(sample.VramUsedBytes), Number(sample.VramTotalBytes), "", "", ""));
    public void WriteEvent(DateTimeOffset timestamp, LlamaRuntimeEvent item) => WriteLine(string.Join('\t', Timestamp(timestamp), "event", "", "", "", "", "", "", "", "", "", "", "", "", "", "", Clean(item.Event), Clean(JsonSerializer.Serialize(item.Fields, Json)), Clean(item.Raw)));
    public void WriteTelemetry(DateTimeOffset timestamp, IReadOnlyDictionary<string, object?> fields, string raw) => WriteLine(string.Join('\t', Timestamp(timestamp), "telemetry", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", Clean(JsonSerializer.Serialize(fields, Json)), Clean(raw)));
    void WriteLine(string value) { lock (gate) writer.WriteLine(value); }
    public void Dispose() { lock (gate) writer.Dispose(); }
    static string UniquePath(string directory, string stem) { for (var suffix = 0; ; suffix++) { var path = System.IO.Path.Combine(directory, suffix == 0 ? stem + ".dat" : $"{stem}-{suffix}.dat"); if (!File.Exists(path)) return path; } }
    static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static string Number<T>(T? value) where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture) ?? "";
    static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
