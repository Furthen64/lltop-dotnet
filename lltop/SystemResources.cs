using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

internal sealed record SystemResourceSnapshot
{
    public double? CpuUsagePercent { get; init; }
    public long? SystemRamUsedBytes { get; init; }
    public long? SystemRamAvailableBytes { get; init; }
    public long? SystemRamTotalBytes { get; init; }
    public long? SwapUsedBytes { get; init; }
    public long? SwapFreeBytes { get; init; }
    public long? LlamaRssBytes { get; init; }
    public long? LlamaPssBytes { get; init; }
    public long? LlamaPrivateDirtyBytes { get; init; }
    public long? LlamaAnonymousBytes { get; init; }
    public long? LlamaSwapBytes { get; init; }
    public double? GpuUsagePercent { get; init; }
    public long? VramUsedBytes { get; init; }
    public long? VramTotalBytes { get; init; }
    public string? GpuName { get; init; }
    public int RunningServerCount { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

internal interface ISystemResourceProvider
{
    Task<SystemResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

internal readonly record struct CpuTimes(long Idle, long Total);
internal readonly record struct GpuResourceMetrics(double? UsagePercent, long VramUsedBytes, long VramTotalBytes, string Name);

internal sealed class LinuxSystemResourceProvider : ISystemResourceProvider
{
    private readonly Func<(string Backend, string Name)> gpuDescription;
    private readonly Func<int> runningServerCount;
    private readonly Func<int?> serverProcessId;
    private readonly Func<CancellationToken, Task<GpuResourceMetrics?>> readGpuMetricsAsync;
    private readonly object cpuGate = new();
    private CpuTimes? previousCpuTimes;

    public LinuxSystemResourceProvider(
        Func<(string Backend, string Name)> gpuDescription,
        Func<int> runningServerCount,
        Func<CancellationToken, Task<GpuResourceMetrics?>>? readGpuMetricsAsync = null,
        Func<int?>? serverProcessId = null)
    {
        this.gpuDescription = gpuDescription;
        this.runningServerCount = runningServerCount;
        this.readGpuMetricsAsync = readGpuMetricsAsync ?? ReadGpuMetricsAsync;
        this.serverProcessId = serverProcessId ?? (() => null);
    }

    public async Task<SystemResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var proc = await Task.Run(() => ReadProcSnapshot(serverProcessId()), cancellationToken);
        var description = gpuDescription();
        // Hardware telemetry must not depend on the selected server/profile probe.
        // That probe can still be pending (or unavailable) even when nvidia-smi works.
        var gpu = await readGpuMetricsAsync(cancellationToken);

        return new SystemResourceSnapshot
        {
            CpuUsagePercent = proc.CpuUsagePercent,
            SystemRamUsedBytes = proc.RamUsedBytes,
            SystemRamAvailableBytes = proc.RamAvailableBytes,
            SystemRamTotalBytes = proc.RamTotalBytes,
            SwapUsedBytes = proc.SwapUsedBytes,
            SwapFreeBytes = proc.SwapFreeBytes,
            LlamaRssBytes = proc.LlamaRssBytes,
            LlamaPssBytes = proc.LlamaPssBytes,
            LlamaPrivateDirtyBytes = proc.LlamaPrivateDirtyBytes,
            LlamaAnonymousBytes = proc.LlamaAnonymousBytes,
            LlamaSwapBytes = proc.LlamaSwapBytes,
            GpuUsagePercent = gpu?.UsagePercent,
            VramUsedBytes = gpu?.VramUsedBytes,
            VramTotalBytes = gpu?.VramTotalBytes,
            GpuName = string.IsNullOrWhiteSpace(gpu?.Name) ? NullIfWhiteSpace(description.Name) : gpu.Value.Name,
            RunningServerCount = Math.Max(0, runningServerCount()),
            Timestamp = DateTimeOffset.Now
        };
    }

    private (double? CpuUsagePercent, long? RamUsedBytes, long? RamAvailableBytes, long? RamTotalBytes, long? SwapUsedBytes, long? SwapFreeBytes, long? LlamaRssBytes, long? LlamaPssBytes, long? LlamaPrivateDirtyBytes, long? LlamaAnonymousBytes, long? LlamaSwapBytes) ReadProcSnapshot(int? processId)
    {
        double? cpuPercent = null;
        var currentCpu = ParseCpuTimes(File.ReadLines("/proc/stat").FirstOrDefault() ?? "");
        if (currentCpu is { } current)
        {
            lock (cpuGate)
            {
                if (previousCpuTimes is { } previous)
                    cpuPercent = CalculateCpuUsagePercent(previous, current);
                previousCpuTimes = current;
            }
        }

        long? total = null, available = null, swapTotal = null, swapFree = null;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = ParseMemInfoBytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = ParseMemInfoBytes(line);
            else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal)) swapTotal = ParseMemInfoBytes(line);
            else if (line.StartsWith("SwapFree:", StringComparison.Ordinal)) swapFree = ParseMemInfoBytes(line);
        }

        long? used = total.HasValue && available.HasValue ? Math.Max(0, total.Value - available.Value) : null;
        var llama = ReadProcessMetrics(processId);
        return (cpuPercent, used, available, total, swapTotal.HasValue && swapFree.HasValue ? Math.Max(0, swapTotal.Value - swapFree.Value) : null, swapFree, llama.Rss, llama.Pss, llama.PrivateDirty, llama.Anonymous, llama.Swap);
    }

    static (long? Rss, long? Pss, long? PrivateDirty, long? Anonymous, long? Swap) ReadProcessMetrics(int? processId)
    {
        if (processId is not > 0) return default;
        try
        {
            var root = $"/proc/{processId.Value}";
            long? Read(string file, string key) => File.ReadLines(file).FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal)) is { } line ? ParseMemInfoBytes(line) : null;
            return (Read($"{root}/status", "VmRSS:"), Read($"{root}/smaps_rollup", "Pss:"), Read($"{root}/smaps_rollup", "Private_Dirty:"), Read($"{root}/smaps_rollup", "Anonymous:"), Read($"{root}/status", "VmSwap:"));
        }
        catch { return default; }
    }

    internal static CpuTimes? ParseCpuTimes(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] != "cpu") return null;
        var values = new long[fields.Length - 1];
        for (var i = 1; i < fields.Length; i++)
            if (!long.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i - 1])) return null;

        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return new CpuTimes(idle, values.Sum());
    }

    internal static double? CalculateCpuUsagePercent(CpuTimes previous, CpuTimes current)
    {
        var totalDelta = current.Total - previous.Total;
        var idleDelta = current.Idle - previous.Idle;
        if (totalDelta <= 0 || idleDelta < 0) return null;
        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0d, 100d);
    }

    internal static long? ParseMemInfoBytes(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib)
            ? kib * 1024L
            : null;
    }

    private static async Task<GpuResourceMetrics?> ReadGpuMetricsAsync(CancellationToken cancellationToken)
    {
        var intel = await ReadXpuSmiMetricsAsync(cancellationToken);
        return intel ?? await ReadNvidiaMetricsAsync(cancellationToken);
    }

    private static async Task<GpuResourceMetrics?> ReadXpuSmiMetricsAsync(CancellationToken cancellationToken)
    {
        var output = await RunCommandAsync("xpu-smi", "stats -d 0", cancellationToken);
        return output is null ? null : ParseXpuSmiMetrics(output);
    }

    // xpu-smi reports memory used and percentage, but not a separate total in its
    // human-readable stats output. Infer the usable device-memory total from them.
    internal static GpuResourceMetrics? ParseXpuSmiMetrics(string output)
    {
        var name = Regex.Match(output, @"Device Name:\s*(.+)");
        var used = Regex.Match(output, @"GPU Memory Used \(MiB\)\s+Tile \d+:\s+(?:avg:\s*)?(\d+)");
        var utilization = Regex.Match(output, @"GPU Memory Util \(%\)\s+Tile \d+:\s+(?:avg:\s*)?(\d+)");
        if (!used.Success || !utilization.Success ||
            !long.TryParse(used.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedMiB) ||
            !double.TryParse(utilization.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var memoryPercent) ||
            memoryPercent is <= 0 or > 100) return null;

        var totalMiB = (long)Math.Round(usedMiB * 100d / memoryPercent, MidpointRounding.AwayFromZero);
        return new GpuResourceMetrics(null, usedMiB * 1024L * 1024L, totalMiB * 1024L * 1024L,
            name.Success ? name.Groups[1].Value.Trim() : "Intel GPU");
    }

    private static async Task<GpuResourceMetrics?> ReadNvidiaMetricsAsync(CancellationToken cancellationToken)
    {
        var output = await RunCommandAsync("nvidia-smi", "--query-gpu=utilization.gpu,memory.used,memory.total,name --format=csv,noheader,nounits", cancellationToken);
        if (string.IsNullOrWhiteSpace(output)) return null;
        var fields = output.Split(',', 4, StringSplitOptions.TrimEntries);
        if (fields.Length != 4 ||
            !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var utilization) ||
            !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedMiB) ||
            !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMiB)) return null;
        return new GpuResourceMetrics(Math.Clamp(utilization, 0, 100), usedMiB * 1024L * 1024L, totalMiB * 1024L * 1024L, fields[3]);
    }

    private static async Task<string?> RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            try
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            }
            catch { }
            process?.Dispose();
        }
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed class UnavailableSystemResourceProvider : ISystemResourceProvider
{
    private readonly Func<int> runningServerCount;

    public UnavailableSystemResourceProvider(Func<int> runningServerCount) => this.runningServerCount = runningServerCount;

    public Task<SystemResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SystemResourceSnapshot { RunningServerCount = Math.Max(0, runningServerCount()), Timestamp = DateTimeOffset.Now });
}
