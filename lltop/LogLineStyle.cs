using Terminal.Gui.Drawing;

internal enum LogLineKind
{
    Normal,
    Hint,
    Error,
    Warning,
    Performance,
    Offload,
    Progress
}

internal static class LogLineStyle
{
    internal static LogLineKind Classify(string line)
    {
        var lower = line.ToLowerInvariant();

        // Keep this ahead of the generic "failed" match, as in the Go parser.
        if (lower.Contains("failed to fit params to free device memory") &&
            lower.Contains("n_gpu_layers already set by user"))
            return LogLineKind.Hint;

        if (lower.Contains("error") || lower.Contains("failed")) return LogLineKind.Error;
        if (lower.Contains("warning") || lower.Contains("warn")) return LogLineKind.Warning;
        if (lower.Contains("tokens per second")) return LogLineKind.Performance;
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
        _ => null
    };
}
