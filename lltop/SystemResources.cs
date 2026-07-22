using System.Diagnostics;
using System.Globalization;

internal sealed record SystemResourceSnapshot
{
    public double? CpuUsagePercent { get; init; }
    public long? SystemRamUsedBytes { get; init; }
    public long? SystemRamTotalBytes { get; init; }
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
internal readonly record struct GpuResourceMetrics(double UsagePercent, long VramUsedBytes, long VramTotalBytes, string Name);

internal sealed class LinuxSystemResourceProvider : ISystemResourceProvider
{
    private readonly Func<(string Backend, string Name)> gpuDescription;
    private readonly Func<int> runningServerCount;
    private readonly object cpuGate = new();
    private CpuTimes? previousCpuTimes;

    public LinuxSystemResourceProvider(
        Func<(string Backend, string Name)> gpuDescription,
        Func<int> runningServerCount)
    {
        this.gpuDescription = gpuDescription;
        this.runningServerCount = runningServerCount;
    }

    public async Task<SystemResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var proc = await Task.Run(ReadProcSnapshot, cancellationToken);
        var description = gpuDescription();
        var gpu = description.Backend.Equals("CUDA", StringComparison.OrdinalIgnoreCase) ||
                  description.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            ? await ReadNvidiaMetricsAsync(cancellationToken)
            : null;

        return new SystemResourceSnapshot
        {
            CpuUsagePercent = proc.CpuUsagePercent,
            SystemRamUsedBytes = proc.RamUsedBytes,
            SystemRamTotalBytes = proc.RamTotalBytes,
            GpuUsagePercent = gpu?.UsagePercent,
            VramUsedBytes = gpu?.VramUsedBytes,
            VramTotalBytes = gpu?.VramTotalBytes,
            GpuName = string.IsNullOrWhiteSpace(gpu?.Name) ? NullIfWhiteSpace(description.Name) : gpu.Value.Name,
            RunningServerCount = Math.Max(0, runningServerCount()),
            Timestamp = DateTimeOffset.Now
        };
    }

    private (double? CpuUsagePercent, long? RamUsedBytes, long? RamTotalBytes) ReadProcSnapshot()
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

        long? total = null;
        long? available = null;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = ParseMemInfoBytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = ParseMemInfoBytes(line);
            if (total.HasValue && available.HasValue) break;
        }

        long? used = total.HasValue && available.HasValue ? Math.Max(0, total.Value - available.Value) : null;
        return (cpuPercent, used, total);
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

    private static async Task<GpuResourceMetrics?> ReadNvidiaMetricsAsync(CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total,name --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
            var line = await outputTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(line)) return null;
            var fields = line.Split(',', 4, StringSplitOptions.TrimEntries);
            if (fields.Length != 4 ||
                !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var utilization) ||
                !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedMiB) ||
                !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMiB)) return null;
            return new GpuResourceMetrics(Math.Clamp(utilization, 0, 100), usedMiB * 1024L * 1024L, totalMiB * 1024L * 1024L, fields[3]);
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
