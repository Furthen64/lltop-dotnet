using System.Text;

internal static class StartupFailureAnalysis
{
    public static string Create(Profile profile, RunRecord run, string logsDirectory)
    {
        var lines = new List<string>
        {
            $"Startup failed after {run.DurationSeconds:F1}s (exit {run.ExitCode}).",
            "",
            "What lltop found"
        };

        var logPath = FindLogPath(profile, run, logsDirectory);
        var logLines = ReadLogLines(logPath);
        var errors = run.Issues.Where(x => x.Severity == "error").Select(x => x.Message)
            .Concat(logLines.Where(x => LlamaLogParser.Parse(x).IsError)).Distinct(StringComparer.Ordinal).Take(3).ToList();
        if (errors.Count > 0)
            foreach (var error in errors) lines.Add($"• {error}");
        else
            lines.Add("• The server exited before lltop recognized a specific llama.cpp error.");

        lines.Add("");
        lines.Add("Likely causes");
        var metadata = ReadMetadata(profile.Model);
        var architecture = metadata.Architecture;
        var identity = metadata.Identity;
        var isDiffusion = IsDiffusionModel(architecture, identity, Path.GetFileName(profile.Model));
        if (isDiffusion)
            lines.Add($"• {architecture} ({identity}) appears to be a diffusion-style model. llama-server is intended for supported autoregressive GGUF language models, so this architecture may not load in this runtime. Use the model's recommended runtime, or a llama.cpp build that explicitly supports it.");
        if (errors.Any(x => x.Contains("failed to load model", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• llama.cpp rejected the model during loading. The log below is the best source for the exact compatibility or file-format reason.");
        if (errors.Any(x => x.Contains("out of memory", StringComparison.OrdinalIgnoreCase)))
            lines.Add("• The selected model and launch settings did not fit available memory. Reduce context or GPU layers, or free memory before retrying.");
        if (profile.NoMmap)
            lines.Add("• Memory mapping is disabled (--no-mmap), so the full model must be read into regular system memory. For a large model, enable mmap unless you specifically need it disabled.");
        if (profile.Ngl == 0)
            lines.Add("• GPU layers is 0, so this profile is configured for CPU-only loading.");
        if (errors.Count == 0 && !isDiffusion && !profile.NoMmap && profile.Ngl != 0)
            lines.Add("• No single cause was recognized. Check the last log lines below, then verify the llama.cpp build supports this GGUF architecture.");

        lines.Add("");
        lines.Add("Profile settings");
        lines.Add($"Model       {Path.GetFileName(profile.Model)}");
        lines.Add($"Architecture {architecture}{(identity.Length == 0 ? "" : $"  ·  {identity}")}");
        lines.Add($"Context     {profile.Ctx:N0}  ·  GPU layers {profile.Ngl}  ·  parallel {profile.Parallel}");
        lines.Add($"Memory map  {(profile.NoMmap ? "disabled" : "enabled")}  ·  batch {profile.Batch}/{profile.UBatch}");
        lines.Add($"Log         {(logPath.Length == 0 ? "not found" : logPath)}");
        if (logLines.Count > 0)
        {
            lines.Add("");
            lines.Add("Last log lines");
            lines.AddRange(logLines.TakeLast(8));
        }
        return string.Join('\n', lines);
    }

    static (string Architecture, string Identity) ReadMetadata(string path)
    {
        try
        {
            var metadata = GgufMetadataReader.Read(path);
            return (metadata.String("general.architecture") ?? "unknown", metadata.String("general.name") ?? "");
        }
        catch { return ("unknown", ""); }
    }

    static bool IsDiffusionModel(string architecture, string identity, string fileName) =>
        architecture.Contains("diffusion", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("diffusion", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("diffusion", StringComparison.OrdinalIgnoreCase);

    static List<string> ReadLogLines(string path)
    {
        try { return File.Exists(path) ? File.ReadLines(path).ToList() : []; }
        catch { return []; }
    }

    static string FindLogPath(Profile profile, RunRecord run, string directory)
    {
        if (!string.IsNullOrWhiteSpace(run.LogPath) && File.Exists(run.LogPath)) return run.LogPath;
        if (!Directory.Exists(directory)) return "";
        var prefix = $"{run.StartedAt.LocalDateTime:yyyy-MM-dd_HHmmss}_{ProfileStore.Slugify(profile.Name)}";
        return Directory.EnumerateFiles(directory, prefix + "*.log").OrderByDescending(x => x, StringComparer.Ordinal).FirstOrDefault() ?? "";
    }
}
