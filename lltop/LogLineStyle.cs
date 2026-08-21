using Terminal.Gui.Drawing;
using System.Text.RegularExpressions;

internal enum LogLineKind
{
    Normal,
    Hint,
    Error,
    Warning,
    Performance,
    Offload,
    Progress,
    MemoryFullyOnGpu,
    MemoryTight,
    MemoryPartialOffload
}

internal static class LogLineStyle
{
    static readonly Regex LlamaSeverity = new(@"(?:^|\s)([IWE])\s+(?=[A-Za-z_])", RegexOptions.Compiled);

    internal static LogLineKind Classify(string line)
    {
        var lower = line.ToLowerInvariant();

        if (lower.Contains("partial offload")) return LogLineKind.MemoryPartialOffload;
        if (lower.Contains("fully on gpu") && (lower.Contains("tight") || lower.Contains("close-to-oom"))) return LogLineKind.MemoryTight;
        if (lower.Contains("fully on gpu")) return LogLineKind.MemoryFullyOnGpu;
        if (lower.Contains("critical:")) return LogLineKind.Error;

        // Keep this ahead of the generic "failed" match, as in the Go parser.
        if (lower.Contains("failed to fit params to free device memory") &&
            lower.Contains("n_gpu_layers already set by user"))
            return LogLineKind.Hint;

        var severity = LlamaSeverity.Match(line);
        if (severity.Success)
            return severity.Groups[1].Value switch
            {
                "E" => LogLineKind.Error,
                "W" => LogLineKind.Warning,
                _ => LogLineKind.Normal
            };

        if (lower.Contains("error") || lower.Contains("failed")) return LogLineKind.Error;
        if (lower.Contains("warning") || lower.Contains("warn")) return LogLineKind.Warning;
        if (lower.Contains("tokens per second") || (lower.Contains("tg =") && lower.Contains("tg_3s ="))) return LogLineKind.Performance;
        if (lower.Contains("offloaded")) return LogLineKind.Offload;
        if (lower.Contains("progress")) return LogLineKind.Progress;
        return LogLineKind.Normal;
    }

    internal static Color? ForegroundFor(string line) => Classify(line) switch
    {
        LogLineKind.Hint or LogLineKind.Warning => LltopTheme.Warning,
        LogLineKind.Error => LltopTheme.Error,
        LogLineKind.Performance => LltopTheme.Success,
        LogLineKind.Offload => LltopTheme.Highlight,
        LogLineKind.Progress => LltopTheme.Muted,
        LogLineKind.MemoryFullyOnGpu => LltopTheme.MemoryFullyOnGpu,
        LogLineKind.MemoryTight => LltopTheme.MemoryTight,
        LogLineKind.MemoryPartialOffload => LltopTheme.MemoryPartialOffload,
        _ => null
    };

    internal static Color? InlineSeverityColor(string line, int column)
    {
        if (ContainsAt(line, "CRITICAL", column)) return LltopTheme.Error;
        if (ContainsAt(line, "WARNING", column)) return LltopTheme.Warning;
        return null;
    }

    static bool ContainsAt(string line, string marker, int column)
    {
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        return start >= 0 && column >= start && column < start + marker.Length;
    }
}
