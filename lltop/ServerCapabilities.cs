using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

enum LaunchArgumentOrigin { Generated, ExtraArgs }

sealed record LaunchArgumentSegment(IReadOnlyList<string> Tokens, LaunchArgumentOrigin Origin, string SourceLabel)
{
    public string FirstToken => Tokens.Count == 0 ? "" : Tokens[0];
}

sealed record RemovedArgument(string OptionName, IReadOnlyList<string> Tokens, string Reason, LaunchArgumentOrigin Origin, string SourceLabel)
{
    public string Display => string.Join(' ', Tokens);
    public bool FromManualArgs => Origin == LaunchArgumentOrigin.ExtraArgs;
}

sealed record LaunchPlan(
    string Executable,
    IReadOnlyList<string> RawArguments,
    IReadOnlyList<string> FilteredArguments,
    IReadOnlyList<RemovedArgument> RemovedArguments,
    ServerCapabilityRecord Capabilities)
{
    public bool HasManualRemovals => RemovedArguments.Any(x => x.FromManualArgs);
}

sealed class ServerCapabilityRecord
{
    public string BinaryPath { get; init; } = "";
    public long BinaryLastWriteTimeUtcTicks { get; init; }
    public long BinaryLength { get; init; }
    public bool BinaryExists { get; init; }
    public bool VersionProbeSucceeded { get; init; }
    public bool HelpProbeSucceeded { get; init; }
    public bool DetectionIncomplete { get; init; }
    public string ProbeMessage { get; init; } = "";
    public string VersionText { get; init; } = "";
    public string HelpText { get; init; } = "";
    public int? BuildNumber { get; init; }
    public string Commit { get; init; } = "";
    public string Backend { get; init; } = "";
    public string GpuName { get; init; } = "";
    public string ComputeCapability { get; init; } = "";
    public Dictionary<string, bool> SupportedOptions { get; init; } = new(StringComparer.Ordinal);

    public bool ProbeSucceeded => VersionProbeSucceeded || HelpProbeSucceeded;
    public bool SupportsOption(string name) => SupportedOptions.ContainsKey(name);
    public bool TryGetOptionArity(string name, out bool expectsValue) => SupportedOptions.TryGetValue(name, out expectsValue);
    public bool IsPascalCuda
    {
        get
        {
            var probeText = $"{VersionText}\n{HelpText}".Replace("\r", "");
            var cuda = Backend.Equals("CUDA", StringComparison.OrdinalIgnoreCase) || probeText.Contains("CUDA", StringComparison.OrdinalIgnoreCase);
            var compute61 = ComputeCapability == "6.1" ||
                            probeText.Contains("compute capability: 6.1", StringComparison.OrdinalIgnoreCase) ||
                            probeText.Contains("compute capability 6.1", StringComparison.OrdinalIgnoreCase);
            return cuda && compute61;
        }
    }

    public string BuildSummary =>
        BuildNumber is null && string.IsNullOrWhiteSpace(Commit) ? "unknown"
        : BuildNumber is null ? Commit
        : string.IsNullOrWhiteSpace(Commit) ? BuildNumber.Value.ToString()
        : $"{BuildNumber.Value} ({Commit})";

    public string CompatibilityMode =>
        !ProbeSucceeded ? "safe fallback"
        : DetectionIncomplete ? "incomplete"
        : IsPascalCuda || !SupportsOption("--reasoning") ? "legacy"
        : "native";
}

sealed record ProbeCommandResult(bool Succeeded, int ExitCode, string Output, string Error)
{
    public static ProbeCommandResult Success(string output, int exitCode = 0) => new(true, exitCode, output, "");
    public static ProbeCommandResult Failure(string error, int exitCode = -1, string output = "") => new(false, exitCode, output, error);
}

interface IServerProbeExecutor
{
    ProbeCommandResult Run(string executable, string argument);
}

sealed class ProcessServerProbeExecutor : IServerProbeExecutor
{
    public ProbeCommandResult Run(string executable, string argument)
    {
        try
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            };
            info.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return ProbeCommandResult.Failure("llama-server did not start.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? ProbeCommandResult.Success(JoinOutput(output, error), process.ExitCode)
                : ProbeCommandResult.Failure(string.IsNullOrWhiteSpace(error) ? $"Exited with code {process.ExitCode}." : error.Trim(), process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return ProbeCommandResult.Failure(ex.Message);
        }
    }

    static string JoinOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return stderr.Trim();
        if (string.IsNullOrWhiteSpace(stderr)) return stdout.Trim();
        return $"{stdout.TrimEnd()}\n{stderr.Trim()}";
    }
}

sealed class ServerCapabilityCache
{
    readonly string cachePath;
    readonly IServerProbeExecutor executor;
    readonly Dictionary<string, ServerCapabilityRecord> cache;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ServerCapabilityCache(string cachePath, IServerProbeExecutor? executor = null)
    {
        this.cachePath = cachePath;
        this.executor = executor ?? new ProcessServerProbeExecutor();
        cache = Load();
    }

    public ServerCapabilityRecord Get(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return new ServerCapabilityRecord { BinaryPath = executable, DetectionIncomplete = true, ProbeMessage = "llama-server path is not configured." };

        var path = Path.GetFullPath(executable);
        if (!File.Exists(path))
            return new ServerCapabilityRecord { BinaryPath = path, DetectionIncomplete = true, ProbeMessage = "llama-server was not found." };

        var info = new FileInfo(path);
        if (cache.TryGetValue(path, out var existing) &&
            existing.BinaryLastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks &&
            existing.BinaryLength == info.Length)
            return existing;

        var record = Probe(path, info);
        cache[path] = record;
        Save();
        return record;
    }

    Dictionary<string, ServerCapabilityRecord> Load()
    {
        try
        {
            if (!File.Exists(cachePath)) return new(StringComparer.Ordinal);
            var loaded = JsonSerializer.Deserialize<List<ServerCapabilityRecord>>(File.ReadAllText(cachePath), JsonOptions) ?? [];
            return loaded.ToDictionary(x => x.BinaryPath, StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
            var temp = cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache.Values.OrderBy(x => x.BinaryPath, StringComparer.Ordinal).ToList(), JsonOptions));
            File.Move(temp, cachePath, true);
        }
        catch { }
    }

    ServerCapabilityRecord Probe(string executable, FileInfo info)
    {
        var version = executor.Run(executable, "--version");
        var help = executor.Run(executable, "--help");
        return ServerCapabilityParser.Parse(executable, info, version, help);
    }
}

static class ServerCapabilityParser
{
    static readonly Regex OptionRegex = new(@"(?<!\S)(--[A-Za-z0-9][A-Za-z0-9-]*|-[A-Za-z0-9][A-Za-z0-9-]*)(?:[=,\s]|$)", RegexOptions.Compiled);
    static readonly Regex BuildRegex = new(@"\b(?:version|build)\s+(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex CommitRegex = new(@"\b([0-9a-f]{7,12})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex ComputeRegex = new(@"compute capability(?:\s*[:=]|\s+)(\d+\.\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly HashSet<string> SafeFallbackOptions =
    [
        "-m", "--model", "--host", "--port", "-a", "-c", "-ngl", "--temp", "--top-p", "--top-k", "--min-p",
        "-b", "-ub", "--parallel", "--threads", "--metrics", "--no-mmap", "--chat-template"
    ];

    static readonly Dictionary<string, bool> KnownOptionArity = new(StringComparer.Ordinal)
    {
        ["-m"] = true, ["--model"] = true, ["--host"] = true, ["--port"] = true, ["-a"] = true, ["-c"] = true,
        ["-ngl"] = true, ["--cache-type-k"] = true, ["--cache-type-v"] = true, ["--flash-attn"] = true, ["-fa"] = true,
        ["--temp"] = true, ["--top-p"] = true, ["--top-k"] = true, ["--min-p"] = true, ["-b"] = true, ["-ub"] = true,
        ["--parallel"] = true, ["--threads"] = true, ["--chat-template"] = true, ["--reasoning"] = true,
        ["--reasoning-budget"] = true, ["--threads-http"] = true, ["--device"] = true, ["--main-gpu"] = true,
        ["-mg"] = true, ["--split-mode"] = true, ["-sm"] = true, ["--tensor-split"] = true, ["-ts"] = true,
        ["--metrics"] = false, ["--jinja"] = false, ["--no-mmap"] = false, ["--verbose"] = false
    };

    public static ServerCapabilityRecord Parse(string executable, FileInfo info, ProbeCommandResult version, ProbeCommandResult help)
    {
        var versionText = version.Succeeded ? version.Output.Trim() : version.Output.Trim();
        var helpText = help.Succeeded ? help.Output.Trim() : help.Output.Trim();
        var combined = string.Join('\n', new[] { version.Output, help.Output }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var supported = help.Succeeded ? ParseOptions(help.Output) : new Dictionary<string, bool>(StringComparer.Ordinal);
        var detectionIncomplete = !version.Succeeded || !help.Succeeded || supported.Count == 0;
        if (supported.Count == 0)
        {
            foreach (var option in SafeFallbackOptions) supported[option] = KnownOptionArity.TryGetValue(option, out var expectsValue) && expectsValue;
        }

        return new ServerCapabilityRecord
        {
            BinaryPath = executable,
            BinaryExists = true,
            BinaryLastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
            BinaryLength = info.Length,
            VersionProbeSucceeded = version.Succeeded,
            HelpProbeSucceeded = help.Succeeded,
            DetectionIncomplete = detectionIncomplete,
            ProbeMessage = BuildProbeMessage(version, help, supported.Count == SafeFallbackOptions.Count && !help.Succeeded),
            VersionText = versionText,
            HelpText = helpText,
            BuildNumber = TryParseBuild(version.Output),
            Commit = TryParseCommit(version.Output),
            Backend = ParseBackend(combined),
            GpuName = ParseGpuName(combined),
            ComputeCapability = ParseComputeCapability(combined),
            SupportedOptions = supported
        };
    }

    public static Dictionary<string, bool> ParseOptions(string helpText)
    {
        var options = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var rawLine in helpText.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (!line.Contains('-', StringComparison.Ordinal)) continue;
            var matches = OptionRegex.Matches(line);
            if (matches.Count == 0) continue;
            var expectsValue = LineSuggestsValue(line);
            foreach (Match match in matches) options[match.Groups[1].Value] = expectsValue;
        }
        return options;
    }

    public static bool TryGetKnownOptionArity(string optionName, out bool expectsValue) => KnownOptionArity.TryGetValue(optionName, out expectsValue);

    static bool LineSuggestsValue(string line)
    {
        var optionList = line;
        var doubleSpace = line.IndexOf("  ", StringComparison.Ordinal);
        if (doubleSpace >= 0) optionList = line[..doubleSpace];
        return optionList.Contains('<') ||
               optionList.Contains('=') ||
               Regex.IsMatch(optionList, @"\s[A-Z][A-Z0-9_-]*(?:\b|])") ||
               Regex.IsMatch(optionList, @"\[[A-Z][A-Z0-9_-]*\]");
    }

    static string BuildProbeMessage(ProbeCommandResult version, ProbeCommandResult help, bool usedFallback)
    {
        var messages = new List<string>();
        if (!version.Succeeded) messages.Add($"--version failed: {version.Error}");
        if (!help.Succeeded) messages.Add($"--help failed: {help.Error}");
        if (usedFallback) messages.Add("using safe fallback option set");
        return string.Join(" | ", messages);
    }

    static int? TryParseBuild(string text)
    {
        var match = BuildRegex.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    static string TryParseCommit(string text)
    {
        var match = CommitRegex.Match(text);
        return match.Success ? match.Groups[1].Value : "";
    }

    static string ParseBackend(string text)
    {
        foreach (var backend in new[] { "CUDA", "Metal", "Vulkan", "HIP", "OpenCL", "SYCL", "CPU" })
            if (text.Contains(backend, StringComparison.OrdinalIgnoreCase)) return backend.ToUpperInvariant();
        var explicitMatch = Regex.Match(text, @"backend\s*[:=]\s*([A-Za-z0-9_+-]+)", RegexOptions.IgnoreCase);
        return explicitMatch.Success ? explicitMatch.Groups[1].Value : "";
    }

    static string ParseGpuName(string text)
    {
        var gpuMatch = Regex.Match(text, @"(?:gpu|device)\s*[:=]\s*(.+)", RegexOptions.IgnoreCase);
        if (gpuMatch.Success) return gpuMatch.Groups[1].Value.Trim();
        var nvidiaMatch = Regex.Match(text, @"(NVIDIA[^\r\n]+)", RegexOptions.IgnoreCase);
        return nvidiaMatch.Success ? nvidiaMatch.Groups[1].Value.Trim() : "";
    }

    static string ParseComputeCapability(string text)
    {
        var match = ComputeRegex.Match(text);
        return match.Success ? match.Groups[1].Value : "";
    }
}
