using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

enum RunnerState { Stopped, Starting, Running, Stopping, Failed }

sealed record ServerExit(int ExitCode, bool Requested, string? Error);

sealed class ServerRunner : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly SemaphoreSlim logGate = new(1, 1);
    Process? process;
    StreamWriter? logWriter;
    Task? completion;
    bool stopRequested;
    int generation;

    public RunnerState State { get; private set; } = RunnerState.Stopped;
    public int? ProcessId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public string Command { get; private set; } = "";
    public string LogPath { get; private set; } = "";
    public ServerExit? LastExit { get; private set; }
    public LaunchPlan? LastPlan { get; private set; }
    public bool IsActive => State is RunnerState.Starting or RunnerState.Running or RunnerState.Stopping;
    public event Action<string>? LineReceived;
    public event Action<RunnerState>? StateChanged;
    public event Action<ServerExit>? Exited;

    public async Task StartAsync(LaunchPlan plan, Profile profile, AppConfig cfg)
    {
        await gate.WaitAsync();
        try
        {
            if (IsActive) throw new InvalidOperationException("A llama-server process is already active.");
            profile.Validate(true, cfg);
            SetState(RunnerState.Starting);
            stopRequested = false;
            LastExit = null;
            var currentGeneration = ++generation;
            var info = new ProcessStartInfo(plan.Executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(plan.Executable) ?? Environment.CurrentDirectory
            };
            foreach (var arg in plan.FilteredArguments) info.ArgumentList.Add(arg);

            Directory.CreateDirectory(cfg.LogsDir);
            LogPath = UniqueLogPath(cfg.LogsDir, profile.Name);
            logWriter = new StreamWriter(new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("llama-server did not start.");
            }
            catch
            {
                process.Dispose(); process = null;
                await logWriter.DisposeAsync(); logWriter = null;
                SetState(RunnerState.Failed);
                throw;
            }
            ProcessId = process.Id;
            StartedAt = DateTimeOffset.Now;
            LastPlan = plan;
            Command = FormatCommand(plan.Executable, plan.FilteredArguments);
            SetState(RunnerState.Running);
            completion = ObserveAsync(process, logWriter, currentGeneration);
        }
        finally { gate.Release(); }
    }

    async Task ObserveAsync(Process observed, StreamWriter writer, int observedGeneration)
    {
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await logGate.WaitAsync();
                try { await writer.WriteLineAsync(line); }
                finally { logGate.Release(); }
                LineReceived?.Invoke(line);
            }
        }
        string? error = null;
        int exitCode;
        try
        {
            await Task.WhenAll(ReadAsync(observed.StandardOutput), ReadAsync(observed.StandardError), observed.WaitForExitAsync());
            exitCode = observed.ExitCode;
        }
        catch (Exception ex) { exitCode = -1; error = ex.Message; }

        await gate.WaitAsync();
        try
        {
            if (generation != observedGeneration) return;
            await writer.DisposeAsync();
            observed.Dispose();
            process = null; logWriter = null; ProcessId = null;
            LastPlan = null;
            SetState(stopRequested || exitCode == 0 ? RunnerState.Stopped : RunnerState.Failed);
        }
        finally { gate.Release(); }
        LastExit = new(exitCode, stopRequested, error);
        Exited?.Invoke(LastExit);
    }

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        Process? active;
        Task? done;
        await gate.WaitAsync();
        try
        {
            if (!IsActive || process is null || process.HasExited) return;
            stopRequested = true;
            SetState(RunnerState.Stopping);
            active = process;
            done = completion;
            if (!TryInterrupt(active)) active.Kill(entireProcessTree: true);
        }
        finally { gate.Release(); }

        if (done is null) return;
        var limit = timeout ?? TimeSpan.FromSeconds(8);
        if (await Task.WhenAny(done, Task.Delay(limit)) != done)
        {
            try { if (!active.HasExited) active.Kill(entireProcessTree: true); } catch { }
            await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    public async Task KillAsync()
    {
        Task? done;
        await gate.WaitAsync();
        try
        {
            if (!IsActive || process is null || process.HasExited) return;
            stopRequested = true; SetState(RunnerState.Stopping); done = completion;
            process.Kill(entireProcessTree: true);
        }
        finally { gate.Release(); }
        if (done is not null) await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(3)));
    }

    static bool TryInterrupt(Process active)
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return kill(active.Id, 2) == 0; } catch { return false; }
    }

    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int signal);

    void SetState(RunnerState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public static IReadOnlyList<string> BuildArguments(Profile p)
        => BuildArgumentSegments(p, new ServerCapabilityRecord()).SelectMany(x => x.Tokens).ToList();

    public static LaunchPlan BuildLaunchPlan(string executable, Profile profile, ServerCapabilityRecord capabilities)
    {
        var segments = BuildArgumentSegments(profile, capabilities);
        var filteredSegments = FilterSegments(segments, capabilities, out var removed);
        return new LaunchPlan(
            executable,
            segments.SelectMany(x => x.Tokens).ToList(),
            filteredSegments.SelectMany(x => x.Tokens).ToList(),
            removed,
            capabilities);
    }

    static IReadOnlyList<LaunchArgumentSegment> BuildArgumentSegments(Profile p, ServerCapabilityRecord capabilities)
    {
        var segments = new List<LaunchArgumentSegment>();
        void Pair(string flag, string value, string sourceLabel)
        {
            if (!string.IsNullOrWhiteSpace(value)) segments.Add(new([flag, value], LaunchArgumentOrigin.Generated, sourceLabel));
        }
        void Flag(string flag, bool enabled, string sourceLabel)
        {
            if (enabled) segments.Add(new([flag], LaunchArgumentOrigin.Generated, sourceLabel));
        }

        segments.Add(new(["-m", p.Model], LaunchArgumentOrigin.Generated, "model"));
        segments.Add(new(["--port", p.Port.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "port"));
        Pair("--host", p.Host, "host");
        Pair("-a", p.Alias, "alias");
        segments.Add(new(["-c", p.Ctx.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "context"));
        segments.Add(new(["-ngl", p.Ngl.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "gpu layers"));
        Pair("--cache-type-k", p.CacheK, "cache K");
        Pair("--cache-type-v", p.CacheV, "cache V");
        Pair("--flash-attn", p.FlashAttn, "flash attention");
        segments.Add(new(["--temp", F(p.Temp)], LaunchArgumentOrigin.Generated, "temperature"));
        segments.Add(new(["--top-p", F(p.TopP)], LaunchArgumentOrigin.Generated, "top P"));
        segments.Add(new(["--top-k", p.TopK.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "top K"));
        segments.Add(new(["--min-p", F(p.MinP)], LaunchArgumentOrigin.Generated, "min P"));
        segments.Add(new(["-b", p.Batch.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "batch"));
        segments.Add(new(["-ub", p.UBatch.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "micro batch"));
        segments.Add(new(["--parallel", p.Parallel.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "parallel"));
        if (p.Threads > 0) segments.Add(new(["--threads", p.Threads.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "threads"));
        Flag("--metrics", p.Metrics, "metrics");
        Flag("--jinja", p.Jinja, "jinja");
        Pair("--reasoning", p.Reasoning, "reasoning");
        segments.Add(new(["--reasoning-budget", p.ReasoningBudget.ToString(CultureInfo.InvariantCulture)], LaunchArgumentOrigin.Generated, "reasoning budget"));
        Flag("--no-mmap", p.NoMmap, "mmap");
        Pair("--chat-template", p.ChatTemplate, "chat template");
        segments.AddRange(ParseExtraArgumentSegments(FilterExtraArguments(p.ExtraArgs), capabilities));
        return segments;
    }

    static IEnumerable<string> FilterExtraArguments(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var value = args[i];
            if (value is "-fa" or "--flash-attn") { if (i + 1 < args.Count && !args[i + 1].StartsWith('-')) i++; continue; }
            if (value.StartsWith("--flash-attn=") || value.StartsWith("-fa=")) continue;
            yield return value;
        }
    }

    static IReadOnlyList<LaunchArgumentSegment> ParseExtraArgumentSegments(IEnumerable<string> args, ServerCapabilityRecord capabilities)
    {
        var segments = new List<LaunchArgumentSegment>();
        var tokens = args.ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i];
            if (!IsOptionToken(current))
            {
                segments.Add(new([current], LaunchArgumentOrigin.ExtraArgs, "extra args"));
                continue;
            }

            var optionName = ExtractOptionName(current);
            if (!string.Equals(optionName, current, StringComparison.Ordinal))
            {
                segments.Add(new([current], LaunchArgumentOrigin.ExtraArgs, "extra args"));
                continue;
            }

            var takesValue = OptionConsumesValue(optionName, capabilities);
            if (takesValue && i + 1 < tokens.Count && IsValueToken(tokens[i + 1]))
                segments.Add(new([current, tokens[++i]], LaunchArgumentOrigin.ExtraArgs, "extra args"));
            else
                segments.Add(new([current], LaunchArgumentOrigin.ExtraArgs, "extra args"));
        }
        return segments;
    }

    static List<LaunchArgumentSegment> FilterSegments(IReadOnlyList<LaunchArgumentSegment> segments, ServerCapabilityRecord capabilities, out List<RemovedArgument> removed)
    {
        var filtered = new List<LaunchArgumentSegment>();
        removed = [];
        foreach (var segment in segments)
        {
            if (segment.Tokens.Count == 0) continue;
            var first = segment.FirstToken;
            if (!IsOptionToken(first))
            {
                filtered.Add(segment);
                continue;
            }

            var optionName = ExtractOptionName(first);
            if (capabilities.SupportsOption(optionName))
            {
                filtered.Add(segment);
                continue;
            }

            removed.Add(new(optionName, segment.Tokens, capabilities.ProbeSucceeded
                ? $"unsupported by {Path.GetFileName(capabilities.BinaryPath)}"
                : "removed by safe fallback compatibility filter", segment.Origin, segment.SourceLabel));
        }
        return filtered;
    }

    static string ExtractOptionName(string token)
    {
        var equals = token.IndexOf('=');
        return equals > 0 ? token[..equals] : token;
    }

    static bool OptionConsumesValue(string optionName, ServerCapabilityRecord capabilities)
    {
        if (capabilities.TryGetOptionArity(optionName, out var fromCapabilities)) return fromCapabilities;
        if (ServerCapabilityParser.TryGetKnownOptionArity(optionName, out var known)) return known;
        return false;
    }

    static bool IsOptionToken(string token) => token.StartsWith('-') && !IsNegativeNumber(token);
    static bool IsValueToken(string token) => !IsOptionToken(token) || IsNegativeNumber(token);
    static bool IsNegativeNumber(string token) => token.Length > 1 && token[0] == '-' && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    static string FormatCommand(string executable, IReadOnlyList<string> args) => string.Join(' ', new[] { executable }.Concat(args).Select(ShellQuote));
    static string ShellQuote(string value) => value.All(c => char.IsLetterOrDigit(c) || "_./:-=".Contains(c)) ? value : "'" + value.Replace("'", "'\\''") + "'";
    static string UniqueLogPath(string dir, string profile)
    {
        var stem = $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{ProfileStore.Slugify(profile)}";
        var path = Path.Combine(dir, stem + ".log");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem}_{n}.log");
        return path;
    }

    public void Dispose()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        try { completion?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        process?.Dispose(); logWriter?.Dispose();
        if (completion is null || completion.IsCompleted) { gate.Dispose(); logGate.Dispose(); }
    }
}
