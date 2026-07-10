using System.Globalization;
using System.Text.RegularExpressions;

internal sealed class ParsedLogLine
{
    public double PromptEvalMs { get; init; }
    public int PromptTokens { get; init; }
    public double PromptMsPerToken { get; init; }
    public double PromptTokensPerSecond { get; init; }
    public double EvalMs { get; init; }
    public int EvalTokens { get; init; }
    public double EvalMsPerToken { get; init; }
    public double EvalTokensPerSecond { get; init; }
    public double TotalMs { get; init; }
    public int TotalTokens { get; init; }
    public int OffloadedLayers { get; init; }
    public int TotalLayers { get; init; }
    public double Progress { get; init; }
    public string ChatFormat { get; init; } = "";
    public int ContextSlotSize { get; init; }
    public int GpuTotalMiB { get; init; }
    public int GpuFreeMiB { get; init; }
    public int GpuModelMiB { get; init; }
    public int GpuContextMiB { get; init; }
    public int GpuComputeMiB { get; init; }
    public bool Cancelled { get; init; }
    public string ErrorKind { get; init; } = "";
    public string ErrorMessage { get; init; } = "";
    public string HintKind { get; init; } = "";
    public string HintMessage { get; init; } = "";
    public bool IsError => ErrorKind.Length > 0;
}

internal static partial class LlamaLogParser
{
    internal static ParsedLogLine Parse(string line)
    {
        var prompt = PromptEval().Match(line);
        var eval = Eval().Match(line);
        var total = Total().Match(line);
        var offload = Offload().Match(line);
        var progress = Progress().Match(line);
        var chat = ChatFormat().Match(line);
        var context = Context().Match(line);
        var memory = Memory().Match(line);
        var lower = line.ToLowerInvariant();
        var hint = lower.Contains("failed to fit params to free device memory") && lower.Contains("n_gpu_layers already set by user");
        var errorKind = hint ? "" : lower switch
        {
            var s when s.Contains("cuda out of memory") => "cuda_oom",
            var s when s.Contains("failed to load model") => "load_model",
            var s when s.Contains("failed to bind") => "bind",
            var s when s.Contains("unknown argument") => "unknown_argument",
            var s when s.Contains("invalid argument") => "invalid_argument",
            var s when s.Contains("cannot open model") => "open_model",
            _ => ""
        };

        return new ParsedLogLine
        {
            PromptEvalMs = D(prompt, 1), PromptTokens = I(prompt, 2), PromptMsPerToken = D(prompt, 3), PromptTokensPerSecond = D(prompt, 4),
            EvalMs = D(eval, 1), EvalTokens = I(eval, 2), EvalMsPerToken = D(eval, 3), EvalTokensPerSecond = D(eval, 4),
            TotalMs = D(total, 1), TotalTokens = I(total, 2), OffloadedLayers = I(offload, 1), TotalLayers = I(offload, 2),
            Progress = D(progress, 3), ChatFormat = chat.Success ? chat.Groups[1].Value.Trim() : "", ContextSlotSize = I(context, 1),
            GpuTotalMiB = I(memory, 2), GpuFreeMiB = I(memory, 3), GpuModelMiB = I(memory, 4), GpuContextMiB = I(memory, 5), GpuComputeMiB = I(memory, 6),
            Cancelled = line.Contains("stop: cancel task", StringComparison.Ordinal), ErrorKind = errorKind,
            ErrorMessage = errorKind.Length > 0 ? line.Trim() : "",
            HintKind = hint ? "gpu_layers_autofit_skipped" : "",
            HintMessage = hint ? "GPU auto-fit was skipped because n_gpu_layers was explicitly configured." : ""
        };
    }

    static double D(Match m, int group) => m.Success && double.TryParse(m.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    static int I(Match m, int group) => m.Success && int.TryParse(m.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex(@"prompt eval time =\s+(\d+\.\d+) ms /\s+(\d+) tokens.*?(\d+\.\d+) ms per token.*?(\d+\.\d+) tokens per second")]
    private static partial Regex PromptEval();
    [GeneratedRegex(@"(?<!prompt )eval time =\s+(\d+\.\d+) ms /\s+(\d+) tokens.*?(\d+\.\d+) ms per token.*?(\d+\.\d+) tokens per second")]
    private static partial Regex Eval();
    [GeneratedRegex(@"total time =\s+(\d+\.\d+) ms /\s+(\d+) tokens")]
    private static partial Regex Total();
    [GeneratedRegex(@"load_tensors: offloaded (\d+)/(\d+) layers to GPU")]
    private static partial Regex Offload();
    [GeneratedRegex(@"prompt processing progress, n_tokens = (\d+), batch\.n_tokens = (\d+), progress = (\d+\.\d+)")]
    private static partial Regex Progress();
    [GeneratedRegex(@"params_from_.*?Chat format: (.+)")]
    private static partial Regex ChatFormat();
    [GeneratedRegex(@"new prompt, n_ctx_slot = (\d+), n_keep = (\d+), task\.n_tokens = (\d+)")]
    private static partial Regex Context();
    [GeneratedRegex(@"llama_memory_breakdown_print.*?\((.+?)\).*?\|\s+(\d+)\s*=\s*(\d+)\s+\+.*?\(\s*(\d+)\s+\+\s+(\d+)\s+\+\s+(\d+)\)")]
    private static partial Regex Memory();
}

internal sealed class ServerStats
{
    public double PromptTokensPerSecond { get; private set; }
    public double EvalTokensPerSecond { get; private set; }
    public int PromptTokens { get; private set; }
    public int GeneratedTokens { get; private set; }
    public int OffloadedLayers { get; private set; }
    public int TotalLayers { get; private set; }
    public double Progress { get; private set; }
    public string ChatFormat { get; private set; } = "";
    public int ContextSlotSize { get; private set; }
    public int GpuTotalMiB { get; private set; }
    public int GpuFreeMiB { get; private set; }
    public int GpuModelMiB { get; private set; }
    public int GpuContextMiB { get; private set; }
    public int GpuComputeMiB { get; private set; }
    public string LastError { get; private set; } = "";
    public string LastHint { get; private set; } = "";
    public List<RunIssue> Issues { get; } = [];

    public void Consume(string line, DateTimeOffset? startedAt = null)
    {
        var p = LlamaLogParser.Parse(line);
        if (p.PromptTokensPerSecond > 0) { PromptTokensPerSecond = p.PromptTokensPerSecond; PromptTokens = p.PromptTokens; }
        if (p.EvalTokensPerSecond > 0) { EvalTokensPerSecond = p.EvalTokensPerSecond; GeneratedTokens = p.EvalTokens; }
        if (p.TotalLayers > 0) { OffloadedLayers = p.OffloadedLayers; TotalLayers = p.TotalLayers; }
        if (p.Progress > 0) Progress = p.Progress;
        if (p.ChatFormat.Length > 0) ChatFormat = p.ChatFormat;
        if (p.ContextSlotSize > 0) ContextSlotSize = p.ContextSlotSize;
        if (p.GpuTotalMiB > 0) { GpuTotalMiB = p.GpuTotalMiB; GpuFreeMiB = p.GpuFreeMiB; GpuModelMiB = p.GpuModelMiB; GpuContextMiB = p.GpuContextMiB; GpuComputeMiB = p.GpuComputeMiB; }
        if (p.HintMessage.Length > 0) LastHint = p.HintMessage;
        if (p.IsError)
        {
            LastError = p.ErrorMessage;
            Issues.Add(new RunIssue("error", p.ErrorKind, p.ErrorMessage, startedAt is null ? 0 : Math.Max(0, (DateTimeOffset.Now - startedAt.Value).TotalSeconds)));
        }
    }
}
