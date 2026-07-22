using System.Globalization;

internal enum ResourceThreshold
{
    Normal,
    Warning,
    Critical
}

internal sealed record ResourceStripSegment(string Text, ResourceThreshold Threshold = ResourceThreshold.Normal);

internal sealed record ResourceStripContent(IReadOnlyList<ResourceStripSegment> Segments)
{
    public string Text => string.Concat(Segments.Select(segment => segment.Text));
}

internal static class ResourceStripFormatter
{
    private const string Separator = " | ";

    internal static ResourceThreshold ThresholdFor(double? percentage) => percentage switch
    {
        >= 90 => ResourceThreshold.Critical,
        >= 75 => ResourceThreshold.Warning,
        _ => ResourceThreshold.Normal
    };

    internal static double? CalculatePercentage(long? used, long? total) =>
        used.HasValue && total is > 0
            ? Math.Clamp(used.Value * 100d / total.Value, 0d, 100d)
            : null;

    internal static string ProgressBar(double? percentage, int width)
    {
        if (!percentage.HasValue || width <= 0) return "";
        var filled = Math.Clamp((int)Math.Round(percentage.Value * width / 100d, MidpointRounding.AwayFromZero), 0, width);
        return "[" + new string('█', filled) + new string('░', width - filled) + "]";
    }

    internal static ResourceStripContent Format(SystemResourceSnapshot snapshot, int width)
    {
        if (width <= 0) return new ResourceStripContent([]);

        for (var barWidth = 12; barWidth >= 3; barWidth--)
        {
            var candidate = BuildDetailed(snapshot, barWidth, includeGpuName: true);
            if (candidate.Text.Length <= width) return candidate;
        }

        var withoutGpuName = BuildDetailed(snapshot, 3, includeGpuName: false);
        if (withoutGpuName.Text.Length <= width) return withoutGpuName;

        var compact = BuildCompact(snapshot, includeBars: true);
        if (compact.Text.Length <= width) return compact;

        var essential = BuildCompact(snapshot, includeBars: false);
        if (essential.Text.Length <= width) return essential;

        return Clip(essential, width);
    }

    private static ResourceStripContent BuildDetailed(SystemResourceSnapshot snapshot, int barWidth, bool includeGpuName)
    {
        var vramPercent = CalculatePercentage(snapshot.VramUsedBytes, snapshot.VramTotalBytes);
        var ramPercent = CalculatePercentage(snapshot.SystemRamUsedBytes, snapshot.SystemRamTotalBytes);
        var segments = new List<ResourceStripSegment>();
        Add(segments, FormatMemory("VRAM", snapshot.VramUsedBytes, snapshot.VramTotalBytes, vramPercent, barWidth), ThresholdFor(vramPercent));

        var gpuName = includeGpuName && !string.IsNullOrWhiteSpace(snapshot.GpuName) ? $" {ShortGpuName(snapshot.GpuName)}" : "";
        Add(segments, $"GPU{gpuName} {Percent(snapshot.GpuUsagePercent)}", ThresholdFor(snapshot.GpuUsagePercent));
        Add(segments, FormatMemory("RAM", snapshot.SystemRamUsedBytes, snapshot.SystemRamTotalBytes, ramPercent, barWidth), ThresholdFor(ramPercent));
        Add(segments, $"CPU {Percent(snapshot.CpuUsagePercent)}", ThresholdFor(snapshot.CpuUsagePercent));
        Add(segments, $"{snapshot.RunningServerCount} RUNNING");
        return new ResourceStripContent(segments);
    }

    private static ResourceStripContent BuildCompact(SystemResourceSnapshot snapshot, bool includeBars)
    {
        var vramPercent = CalculatePercentage(snapshot.VramUsedBytes, snapshot.VramTotalBytes);
        var ramPercent = CalculatePercentage(snapshot.SystemRamUsedBytes, snapshot.SystemRamTotalBytes);
        var vramBar = includeBars && vramPercent.HasValue ? $" {ProgressBar(vramPercent, 3)}" : "";
        var ramBar = includeBars && ramPercent.HasValue ? $" {ProgressBar(ramPercent, 3)}" : "";
        var segments = new List<ResourceStripSegment>();
        Add(segments, $"V{vramBar} {Percent(vramPercent)}", ThresholdFor(vramPercent));
        Add(segments, $"G {Percent(snapshot.GpuUsagePercent)}", ThresholdFor(snapshot.GpuUsagePercent));
        Add(segments, $"R{ramBar} {Percent(ramPercent)}", ThresholdFor(ramPercent));
        Add(segments, $"C {Percent(snapshot.CpuUsagePercent)}", ThresholdFor(snapshot.CpuUsagePercent));
        Add(segments, $"{snapshot.RunningServerCount} RUN");
        return new ResourceStripContent(segments);
    }

    private static void Add(List<ResourceStripSegment> segments, string text, ResourceThreshold threshold = ResourceThreshold.Normal)
    {
        if (segments.Count > 0) segments.Add(new ResourceStripSegment(Separator));
        segments.Add(new ResourceStripSegment(text, threshold));
    }

    private static string FormatMemory(string label, long? used, long? total, double? percentage, int barWidth)
    {
        if (!used.HasValue || total is not > 0 || !percentage.HasValue) return $"{label} N/A";
        return $"{label} {ProgressBar(percentage, barWidth)} {GiB(used.Value)}/{GiB(total.Value)}G {Percent(percentage)}";
    }

    private static string GiB(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);
    private static string Percent(double? value) => value.HasValue ? $"{Math.Clamp((int)Math.Round(value.Value), 0, 100)}%" : "N/A";

    private static string ShortGpuName(string name)
    {
        var compact = name.Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase).Trim();
        return compact.Length <= 20 ? compact : compact[..19] + "…";
    }

    private static ResourceStripContent Clip(ResourceStripContent content, int width)
    {
        var remaining = width;
        var clipped = new List<ResourceStripSegment>();
        foreach (var segment in content.Segments)
        {
            if (remaining <= 0) break;
            var text = segment.Text.Length <= remaining ? segment.Text : segment.Text[..remaining];
            clipped.Add(segment with { Text = text });
            remaining -= text.Length;
        }
        return new ResourceStripContent(clipped);
    }
}
