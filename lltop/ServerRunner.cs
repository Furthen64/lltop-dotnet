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
    public bool IsActive => State is RunnerState.Starting or RunnerState.Running or RunnerState.Stopping;
    public event Action<string>? LineReceived;
    public event Action<RunnerState>? StateChanged;
    public event Action<ServerExit>? Exited;

    public async Task StartAsync(AppConfig cfg, Profile profile)
    {
        await gate.WaitAsync();
        try
        {
            if (IsActive) throw new InvalidOperationException("A llama-server process is already active.");
            profile.Validate(true, cfg);
            SetState(RunnerState.Starting);
            stopRequested = false;
            var currentGeneration = ++generation;
            var executable = string.IsNullOrWhiteSpace(profile.LlamaServer) ? cfg.LlamaServer : profile.LlamaServer;
            var args = BuildArguments(profile);
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);

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
            Command = FormatCommand(executable, args);
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
            SetState(stopRequested || exitCode == 0 ? RunnerState.Stopped : RunnerState.Failed);
        }
        finally { gate.Release(); }
        Exited?.Invoke(new(exitCode, stopRequested, error));
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
    {
        var a = new List<string> { "-m", p.Model, "--port", p.Port.ToString(CultureInfo.InvariantCulture) };
        void Pair(string flag, string value) { if (!string.IsNullOrWhiteSpace(value)) { a.Add(flag); a.Add(value); } }
        Pair("--host", p.Host); Pair("-a", p.Alias);
        a.AddRange(["-c", p.Ctx.ToString(CultureInfo.InvariantCulture), "-ngl", p.Ngl.ToString(CultureInfo.InvariantCulture)]);
        Pair("--cache-type-k", p.CacheK); Pair("--cache-type-v", p.CacheV); Pair("--flash-attn", p.FlashAttn);
        a.AddRange(["--temp", F(p.Temp), "--top-p", F(p.TopP), "--top-k", p.TopK.ToString(CultureInfo.InvariantCulture),
            "--min-p", F(p.MinP), "-b", p.Batch.ToString(CultureInfo.InvariantCulture), "-ub", p.UBatch.ToString(CultureInfo.InvariantCulture),
            "--parallel", p.Parallel.ToString(CultureInfo.InvariantCulture)]);
        if (p.Threads > 0) a.AddRange(["--threads", p.Threads.ToString(CultureInfo.InvariantCulture)]);
        if (p.Metrics) a.Add("--metrics");
        if (p.Jinja) a.Add("--jinja");
        Pair("--reasoning", p.Reasoning);
        a.AddRange(["--reasoning-budget", p.ReasoningBudget.ToString(CultureInfo.InvariantCulture)]);
        if (p.NoMmap) a.Add("--no-mmap");
        Pair("--chat-template", p.ChatTemplate);
        a.AddRange(FilterExtraArguments(p.ExtraArgs));
        return a;
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
