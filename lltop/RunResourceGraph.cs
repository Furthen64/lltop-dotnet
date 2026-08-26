using System.Globalization;
using System.Text;

internal sealed record RunResourceSample
{
    public DateTimeOffset Timestamp { get; init; }
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

    internal static RunResourceSample From(SystemResourceSnapshot snapshot) => new()
    {
        Timestamp = snapshot.Timestamp,
        CpuUsagePercent = snapshot.CpuUsagePercent,
        SystemRamUsedBytes = snapshot.SystemRamUsedBytes,
        SystemRamAvailableBytes = snapshot.SystemRamAvailableBytes,
        SystemRamTotalBytes = snapshot.SystemRamTotalBytes,
        SwapUsedBytes = snapshot.SwapUsedBytes,
        SwapFreeBytes = snapshot.SwapFreeBytes,
        LlamaRssBytes = snapshot.LlamaRssBytes,
        LlamaPssBytes = snapshot.LlamaPssBytes,
        LlamaPrivateDirtyBytes = snapshot.LlamaPrivateDirtyBytes,
        LlamaAnonymousBytes = snapshot.LlamaAnonymousBytes,
        LlamaSwapBytes = snapshot.LlamaSwapBytes,
        GpuUsagePercent = snapshot.GpuUsagePercent,
        VramUsedBytes = snapshot.VramUsedBytes,
        VramTotalBytes = snapshot.VramTotalBytes
    };
}

internal static class RunResourceGraph
{
    private const int Height = 10;

    internal static string Format(string profileName, RunRecord? run, IReadOnlyList<RunResourceSample> samples, int availableWidth, bool live)
    {
        if (samples.Count == 0)
            return live
                ? $"Resource graph · {profileName}\n\nWaiting for the first resource sample…"
                : $"Resource graph · {profileName}\n\nNo resource samples were saved for the latest run.\nOnly runs started after this feature was added contain graphs.";

        var width = Math.Clamp(availableWidth - 13, 12, 100);
        var started = run?.StartedAt ?? samples[0].Timestamp;
        var ended = run?.EndedAt ?? samples[^1].Timestamp;
        var elapsed = Math.Max(0, (ended - started).TotalSeconds);
        var title = live ? "LIVE" : run is null ? "LATEST" : $"exit {run.ExitCode}";
        var lines = new List<string>
        {
            $"Resource graph · {profileName} · {title}",
            $"{samples.Count} samples over {elapsed:F0}s  (each column is a point in time)",
            ""
        };
        AddChart(lines, "VRAM", samples, x => Percent(x.VramUsedBytes, x.VramTotalBytes), x => x.VramUsedBytes, x => x.VramTotalBytes, width);
        lines.Add("");
        AddChart(lines, "SYS RAM", samples, x => Percent(x.SystemRamUsedBytes, x.SystemRamTotalBytes), x => x.SystemRamUsedBytes, x => x.SystemRamTotalBytes, width);
        lines.Add("");
        lines.Add("[g] Back to runtime log   Peak values show the highest sampled use.");
        return string.Join('\n', lines);
    }

    private static void AddChart(List<string> lines, string label, IReadOnlyList<RunResourceSample> samples,
        Func<RunResourceSample, double?> percent, Func<RunResourceSample, long?> used, Func<RunResourceSample, long?> total, int width)
    {
        var values = Downsample(samples.Select(percent).ToList(), width);
        var peakIndex = samples.Select(percent).Select((value, index) => (value, index)).Where(x => x.value.HasValue).OrderByDescending(x => x.value).FirstOrDefault().index;
        var peak = peakIndex >= 0 && peakIndex < samples.Count ? percent(samples[peakIndex]) : null;
        var peakUsed = peakIndex >= 0 && peakIndex < samples.Count ? used(samples[peakIndex]) : null;
        var peakTotal = peakIndex >= 0 && peakIndex < samples.Count ? total(samples[peakIndex]) : null;
        if (!peak.HasValue)
        {
            lines.Add($"{label}  telemetry unavailable");
            return;
        }

        lines.Add($"{label}  peak {GiB(peakUsed)}/{GiB(peakTotal)} GiB ({peak.Value:F0}%)");
        for (var row = Height; row >= 0; row--)
        {
            var threshold = row * 100d / Height;
            var plot = new string(values.Select(value => value.HasValue && value.Value >= threshold ? '#' : ' ').ToArray());
            lines.Add($"{threshold,3:F0}% |{plot}");
        }
        lines.Add($"     +{new string('-', values.Count)}");
        lines.Add("      start" + new string(' ', Math.Max(1, values.Count - 8)) + "end");
    }

    private static List<double?> Downsample(IReadOnlyList<double?> values, int width)
    {
        if (values.Count <= width) return values.ToList();
        var result = new List<double?>(width);
        for (var column = 0; column < width; column++)
        {
            var first = column * values.Count / width;
            var last = Math.Max(first + 1, (column + 1) * values.Count / width);
            result.Add(values.Skip(first).Take(last - first).Where(x => x.HasValue).DefaultIfEmpty().Max());
        }
        return result;
    }

    private static double? Percent(long? used, long? total) => ResourceStripFormatter.CalculatePercentage(used, total);
    private static string GiB(long? bytes) => bytes.HasValue ? (bytes.Value / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) : "N/A";
}
