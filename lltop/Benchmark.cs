using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum BenchmarkCaseStatus { Pending, Running, Completed, Failed, Cancelled, OutOfMemory, Skipped }
internal enum BenchmarkOomPolicy { Stop, Continue }
internal sealed record BenchmarkSetup(List<BenchmarkSweep> Sweeps, BenchmarkWorkload Workload, BenchmarkOomPolicy OomPolicy);

internal sealed class BenchmarkWorkload
{
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "Reply with a short benchmark acknowledgement.";
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 32;
    [JsonPropertyName("temperature")] public double Temperature { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Prompt)) throw new InvalidOperationException("Benchmark prompt is required.");
        if (MaxTokens is < 1 or > 32768) throw new InvalidOperationException("Benchmark max tokens must be between 1 and 32768.");
        if (Temperature < 0) throw new InvalidOperationException("Benchmark temperature cannot be negative.");
    }
}

internal sealed class BenchmarkSweep
{
    [JsonPropertyName("setting")] public string Setting { get; set; } = "";
    [JsonPropertyName("minimum")] public string Minimum { get; set; } = "";
    [JsonPropertyName("maximum")] public string Maximum { get; set; } = "";
    [JsonPropertyName("values")] public List<string> Values { get; set; } = [];

    public bool IsCategorical => Values.Count > 0;
}

internal sealed class BenchmarkCase
{
    [JsonPropertyName("case_id")] public string CaseId { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("setting")] public string Setting { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("profile")] public Profile Profile { get; set; } = new();
    [JsonPropertyName("status")] public BenchmarkCaseStatus Status { get; set; } = BenchmarkCaseStatus.Pending;
    [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("ended_at")] public DateTimeOffset? EndedAt { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("telemetry_available")] public bool TelemetryAvailable { get; set; }
    [JsonPropertyName("vram_samples_bytes")] public List<long> VramSamplesBytes { get; set; } = [];
    [JsonPropertyName("vram_used_bytes")] public long? VramUsedBytes { get; set; }
    [JsonPropertyName("vram_total_bytes")] public long? VramTotalBytes { get; set; }
    [JsonPropertyName("server_log_path")] public string ServerLogPath { get; set; } = "";
}

internal sealed class BenchmarkRecord
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("benchmark_id")] public string BenchmarkId { get; set; } = "";
    [JsonPropertyName("profile_name")] public string ProfileName { get; set; } = "";
    [JsonPropertyName("baseline_profile")] public Profile BaselineProfile { get; set; } = new();
    [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; set; }
    [JsonPropertyName("ended_at")] public DateTimeOffset? EndedAt { get; set; }
    [JsonPropertyName("status")] public BenchmarkCaseStatus Status { get; set; } = BenchmarkCaseStatus.Pending;
    [JsonPropertyName("workload")] public BenchmarkWorkload Workload { get; set; } = new();
    [JsonPropertyName("oom_policy")] public BenchmarkOomPolicy OomPolicy { get; set; } = BenchmarkOomPolicy.Stop;
    [JsonPropertyName("sweeps")] public List<BenchmarkSweep> Sweeps { get; set; } = [];
    [JsonPropertyName("cases")] public List<BenchmarkCase> Cases { get; set; } = [];
    [JsonPropertyName("json_report")] public string JsonReport { get; set; } = "";
    [JsonPropertyName("html_report")] public string HtmlReport { get; set; } = "";
}

internal static class BenchmarkCases
{
    static readonly Dictionary<string, Action<Profile, string>> Setters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctx"] = (p, v) => p.Ctx = PositiveInt(v, "Context"),
        ["ngl"] = (p, v) => p.Ngl = NonNegativeInt(v, "GPU layers"),
        ["batch"] = (p, v) => p.Batch = PositiveInt(v, "Batch"),
        ["ubatch"] = (p, v) => p.UBatch = PositiveInt(v, "Micro batch"),
        ["parallel"] = (p, v) => p.Parallel = PositiveInt(v, "Parallel"),
        ["cache_k"] = (p, v) => p.CacheK = Cache(v, "Cache K"),
        ["cache_v"] = (p, v) => p.CacheV = Cache(v, "Cache V"),
        ["flash_attn"] = (p, v) => p.FlashAttn = Choice(v, "Flash attention", "", "auto", "on", "off")
    };

    public static IReadOnlyCollection<string> SupportedSettings => Setters.Keys;

    public static List<BenchmarkCase> Generate(Profile baseline, IEnumerable<BenchmarkSweep> sweeps)
    {
        var result = new List<BenchmarkCase> { Create("baseline", "Baseline", "", "", baseline.Copy(baseline.Name)) };
        foreach (var sweep in sweeps)
        {
            var setting = sweep.Setting.Trim().ToLowerInvariant();
            if (!Setters.ContainsKey(setting)) throw new InvalidOperationException($"Unsupported benchmark setting '{sweep.Setting}'.");
            var values = Values(sweep, setting);
            foreach (var value in values)
            {
                var profile = baseline.Copy(baseline.Name);
                Setters[setting](profile, value);
                profile.Validate();
                if (SameValue(baseline, setting, value)) continue;
                result.Add(Create($"{setting}-{ProfileStore.Slugify(value)}", $"{setting} = {value}", setting, value, profile));
            }
        }
        return result;
    }

    static BenchmarkCase Create(string id, string label, string setting, string value, Profile profile) =>
        new() { CaseId = id, Label = label, Setting = setting, Value = value, Profile = profile };

    static bool SameValue(Profile baseline, string setting, string value)
    {
        var copy = baseline.Copy(baseline.Name);
        Setters[setting](copy, value);
        return setting switch
        {
            "ctx" => copy.Ctx == baseline.Ctx,
            "ngl" => copy.Ngl == baseline.Ngl,
            "batch" => copy.Batch == baseline.Batch,
            "ubatch" => copy.UBatch == baseline.UBatch,
            "parallel" => copy.Parallel == baseline.Parallel,
            "cache_k" => copy.CacheK.Equals(baseline.CacheK, StringComparison.OrdinalIgnoreCase),
            "cache_v" => copy.CacheV.Equals(baseline.CacheV, StringComparison.OrdinalIgnoreCase),
            "flash_attn" => copy.FlashAttn.Equals(baseline.FlashAttn, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    static List<string> Values(BenchmarkSweep sweep, string setting)
    {
        if (sweep.IsCategorical)
        {
            if (sweep.Values.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException($"{setting} contains an empty value.");
            return sweep.Values.Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (!int.TryParse(sweep.Minimum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum) ||
            !int.TryParse(sweep.Maximum, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum))
            throw new InvalidOperationException($"{setting} requires integer minimum and maximum values.");
        if (minimum > maximum) throw new InvalidOperationException($"{setting} minimum cannot exceed maximum.");
        var middle = minimum + (maximum - minimum) / 2;
        return new[] { minimum.ToString(CultureInfo.InvariantCulture), middle.ToString(CultureInfo.InvariantCulture), maximum.ToString(CultureInfo.InvariantCulture) }
            .Distinct(StringComparer.Ordinal).ToList();
    }

    static int PositiveInt(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : throw new InvalidOperationException($"{name} must be a positive integer.");
    static int NonNegativeInt(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : throw new InvalidOperationException($"{name} must be zero or greater.");
    static string Cache(string value, string name) => Choice(value, name, "", "f16", "q8_0", "q4_0", "iq4_nl");
    static string Choice(string value, string name, params string[] allowed) =>
        allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? value.Trim().ToLowerInvariant() : throw new InvalidOperationException($"{name} has an unsupported value '{value}'.");
}

internal static class BenchmarkStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string SaveJson(string directory, BenchmarkRecord benchmark)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{benchmark.StartedAt:yyyy-MM-dd_HHmmss}_{ProfileStore.Slugify(benchmark.ProfileName)}_benchmark.json");
        File.WriteAllText(path, JsonSerializer.Serialize(benchmark, Json) + Environment.NewLine);
        return path;
    }

    public static BenchmarkRecord Load(string path) =>
        JsonSerializer.Deserialize<BenchmarkRecord>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"Invalid benchmark record: {path}");
}

internal static class BenchmarkReport
{
    public static string SaveHtml(string directory, BenchmarkRecord benchmark)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{benchmark.StartedAt:yyyy-MM-dd_HHmmss}_{ProfileStore.Slugify(benchmark.ProfileName)}_benchmark.html");
        File.WriteAllText(path, Html(benchmark));
        return path;
    }

    public static string Html(BenchmarkRecord benchmark)
    {
        var json = JsonSerializer.Serialize(benchmark, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });
        var rows = string.Join("", benchmark.Cases.Select(c => $"<tr><td>{E(c.Label)}</td><td>{E(c.Status.ToString())}</td><td>{E(FormatVram(c))}</td><td>{E(Headroom(c))}</td><td>{E(c.Error)}</td></tr>"));
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><title>lltop benchmark {E(benchmark.ProfileName)}</title><style>body{{font:16px system-ui;margin:2rem;color:#1f2937}}table{{border-collapse:collapse;width:100%}}th,td{{border:1px solid #d1d5db;padding:.5rem;text-align:left}}th{{background:#f3f4f6}}.warning{{color:#a16207;font-weight:600}}code{{white-space:pre-wrap}}</style></head><body><h1>Benchmark: {E(benchmark.ProfileName)}</h1><p>Status: <b>{E(benchmark.Status.ToString())}</b> · Started: {E(benchmark.StartedAt.LocalDateTime.ToString("u"))}</p><p>Workload: {E(benchmark.Workload.Prompt)} · max tokens {benchmark.Workload.MaxTokens}</p><table><thead><tr><th>Case</th><th>Status</th><th>Post-warmup VRAM</th><th>Headroom / risk</th><th>Error</th></tr></thead><tbody>{rows}</tbody></table><h2>Embedded data</h2><code id=\"data\"></code><script>document.getElementById('data').textContent=JSON.stringify({json},null,2);</script></body></html>";
    }

    static string E(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
    static string FormatBytes(long? value) => value is null ? "" : $"{value.Value / 1024d / 1024d:F1} MiB";
    internal static string FormatVram(BenchmarkCase item) => !item.TelemetryAvailable ? "unavailable" : item.VramTotalBytes is > 0
        ? $"{FormatBytes(item.VramUsedBytes)} / {FormatBytes(item.VramTotalBytes)} ({item.VramUsedBytes.GetValueOrDefault() * 100d / item.VramTotalBytes.Value:F0}%)"
        : FormatBytes(item.VramUsedBytes);
    internal static string Headroom(BenchmarkCase item)
    {
        if (!item.TelemetryAvailable || item.VramTotalBytes is not > 0) return "total unavailable";
        var usedPercent = item.VramUsedBytes.GetValueOrDefault() * 100d / item.VramTotalBytes.Value;
        var free = FormatBytes(Math.Max(0, item.VramTotalBytes.Value - item.VramUsedBytes.GetValueOrDefault()));
        return usedPercent >= 90 ? $"CRITICAL: {free} free" : usedPercent >= 80 ? $"WARNING: {free} free" : $"{free} free";
    }
}

internal sealed class BenchmarkRunner
{
    readonly AppConfig config;
    readonly Func<Profile, LaunchPlan> createPlan;
    readonly Func<bool> serverIsActive;
    readonly ISystemResourceProvider telemetry;
    readonly HttpClient httpClient;

    public BenchmarkRunner(AppConfig config, Func<Profile, LaunchPlan> createPlan, Func<bool> serverIsActive, ISystemResourceProvider telemetry, HttpClient? httpClient = null)
    {
        this.config = config; this.createPlan = createPlan; this.serverIsActive = serverIsActive; this.telemetry = telemetry;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task RunAsync(BenchmarkRecord benchmark, Action<BenchmarkRecord>? progress, CancellationToken cancellationToken)
    {
        if (serverIsActive()) throw new InvalidOperationException("Stop the active llama-server before starting a benchmark.");
        benchmark.Workload.Validate();
        benchmark.Cases = BenchmarkCases.Generate(benchmark.BaselineProfile, benchmark.Sweeps);
        benchmark.Status = BenchmarkCaseStatus.Running;
        progress?.Invoke(benchmark);
        foreach (var item in benchmark.Cases)
        {
            if (cancellationToken.IsCancellationRequested) { item.Status = BenchmarkCaseStatus.Cancelled; break; }
            await RunCaseAsync(item, benchmark.Workload, cancellationToken);
            progress?.Invoke(benchmark);
            if (item.Status == BenchmarkCaseStatus.OutOfMemory && benchmark.OomPolicy == BenchmarkOomPolicy.Stop)
            {
                foreach (var remaining in benchmark.Cases.SkipWhile(x => !ReferenceEquals(x, item)).Skip(1)) remaining.Status = BenchmarkCaseStatus.Skipped;
                break;
            }
        }
        benchmark.Status = benchmark.Cases.Any(x => x.Status == BenchmarkCaseStatus.Cancelled) ? BenchmarkCaseStatus.Cancelled
            : benchmark.Cases.Any(x => x.Status == BenchmarkCaseStatus.OutOfMemory) ? BenchmarkCaseStatus.OutOfMemory
            : benchmark.Cases.Any(x => x.Status == BenchmarkCaseStatus.Failed) ? BenchmarkCaseStatus.Failed : BenchmarkCaseStatus.Completed;
        benchmark.EndedAt = DateTimeOffset.Now;
        benchmark.JsonReport = BenchmarkStore.SaveJson(config.BenchmarksDir, benchmark);
        benchmark.HtmlReport = BenchmarkReport.SaveHtml(config.BenchmarksDir, benchmark);
        BenchmarkStore.SaveJson(config.BenchmarksDir, benchmark);
        progress?.Invoke(benchmark);
    }

    async Task RunCaseAsync(BenchmarkCase item, BenchmarkWorkload workload, CancellationToken cancellationToken)
    {
        item.Status = BenchmarkCaseStatus.Running; item.StartedAt = DateTimeOffset.Now;
        var runner = new ServerRunner();
        var lines = new List<string>();
        runner.LineReceived += line => lines.Add(line);
        try
        {
            await runner.StartAsync(createPlan(item.Profile), item.Profile, config);
            item.ServerLogPath = runner.LogPath;
            await WaitReadyAsync(item.Profile, runner, cancellationToken);
            await WarmupAsync(item.Profile, workload, cancellationToken);
            var until = DateTimeOffset.Now.AddSeconds(10);
            while (DateTimeOffset.Now < until)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await telemetry.GetSnapshotAsync(cancellationToken);
                if (snapshot.VramUsedBytes is { } vram) item.VramSamplesBytes.Add(vram);
                item.VramTotalBytes ??= snapshot.VramTotalBytes;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            item.TelemetryAvailable = item.VramSamplesBytes.Count > 0;
            item.VramUsedBytes = item.TelemetryAvailable ? item.VramSamplesBytes.Max() : null;
            item.Status = IsOom(lines, runner.LastExit) ? BenchmarkCaseStatus.OutOfMemory : BenchmarkCaseStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { item.Status = BenchmarkCaseStatus.Cancelled; }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            item.Status = IsOom(lines, runner.LastExit, ex.Message) ? BenchmarkCaseStatus.OutOfMemory : BenchmarkCaseStatus.Failed;
        }
        finally
        {
            await runner.StopAsync();
            runner.Dispose();
            item.EndedAt = DateTimeOffset.Now;
        }
    }

    async Task WaitReadyAsync(Profile profile, ServerRunner runner, CancellationToken token)
    {
        var endpoint = BaseUri(profile) + "/health";
        var deadline = DateTimeOffset.Now.AddSeconds(300);
        while (DateTimeOffset.Now < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!runner.IsActive) throw new InvalidOperationException($"llama-server stopped before becoming ready (exit {runner.LastExit?.ExitCode}).");
            try { if ((await httpClient.GetAsync(endpoint, token)).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
            await Task.Delay(1000, token);
        }
        throw new TimeoutException("llama-server did not become ready within 300 seconds.");
    }

    async Task WarmupAsync(Profile profile, BenchmarkWorkload workload, CancellationToken token)
    {
        var body = new { messages = new[] { new { role = "user", content = workload.Prompt } }, max_tokens = workload.MaxTokens, temperature = workload.Temperature, stream = false };
        using var response = await httpClient.PostAsJsonAsync(BaseUri(profile) + "/v1/chat/completions", body, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Warmup request failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    static string BaseUri(Profile profile)
    {
        var host = profile.Host is "0.0.0.0" or "::" ? "127.0.0.1" : profile.Host;
        return $"http://{host}:{profile.Port}";
    }

    static bool IsOom(IEnumerable<string> lines, ServerExit? exit, string extra = "")
    {
        var text = string.Join('\n', lines.Append(extra)).ToLowerInvariant();
        return text.Contains("out of memory") || text.Contains("cuda error") || text.Contains("hip error") || text.Contains("failed to allocate") || text.Contains("failed to fit params");
    }
}
