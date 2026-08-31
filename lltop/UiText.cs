static class UiText
{
    public static string ProfileGlyph(bool isBroken, bool isRunning) =>
        isBroken ? "💥" : isRunning ? "●" : "○";

    public sealed record ProfileRowData(string Marker, bool Vision, string Name, IReadOnlyList<string> Tags, string Size);

    // Columns: glyph · [V] slot · profile name · tags · image size. The [V] slot and the
    // name column keep a fixed width so tags line up across rows; tags truncate before names do.
    public static List<string> ProfileRows(IEnumerable<ProfileRowData> source, int width)
    {
        var lines = new List<string>();
        width = Math.Max(12, width);
        var parts = source.Select(r =>
        {
            var prefix = r.Marker + " " + (r.Vision ? "[V] " : "    ");
            var suffix = string.IsNullOrWhiteSpace(r.Size) ? "" : $" {r.Size}";
            return (R: r, Prefix: prefix, Suffix: suffix, Avail: Math.Max(1, width - prefix.Length - suffix.Length));
        }).ToList();
        if (parts.Count == 0) return lines;
        var hasTags = parts.Any(p => p.R.Tags.Any(t => !string.IsNullOrWhiteSpace(t)));
        var nameWidth = hasTags
            ? Math.Clamp(parts.Max(p => MiddleEllipsize(p.R.Name, p.Avail).Length), 0, Math.Max(0, parts.Min(p => p.Avail) - 1))
            : 0;
        foreach (var part in parts)
        {
            if (part.Prefix.Length + part.Suffix.Length >= width)
            {
                lines.Add(MiddleEllipsize(part.Prefix + part.R.Name + part.Suffix, width));
                continue;
            }
            if (!hasTags)
            {
                lines.Add(part.Prefix + MiddleEllipsize(part.R.Name, part.Avail).PadRight(part.Avail) + part.Suffix);
                continue;
            }
            var tagText = string.Join(", ", part.R.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
            var middle = MiddleEllipsize(part.R.Name, nameWidth).PadRight(nameWidth);
            if (tagText.Length > 0) middle += " " + MiddleEllipsize(tagText, Math.Max(0, part.Avail - nameWidth - 1));
            lines.Add(part.Prefix + middle.PadRight(part.Avail) + part.Suffix);
        }
        return lines;
    }

    public static string MiddleEllipsize(string value, int width)
    {
        if (width <= 0) return "";
        if (value.Length <= width) return value;
        if (width <= 3) return value[..width];
        var content = width - 1;
        var left = (content + 1) / 2;
        var right = content - left;
        return value[..left] + "…" + (right == 0 ? "" : value[^right..]);
    }

    public static string RelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var elapsed = now - value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed < TimeSpan.FromDays(7)) return $"{(int)elapsed.TotalDays}d ago";
        return value.LocalDateTime.ToString("yyyy-MM-dd");
    }

    public static string RequestMetrics(ServerStats stats)
    {
        if (stats.Progress is > 0 and < 1)
        {
            var activeInput = $"reading {stats.Progress:P0}";
            if (stats.PromptProgressTokens > 0) activeInput += $"  ·  {stats.PromptProgressTokens:N0} tokens";
            if (stats.PromptProgressTokensPerSecond > 0) activeInput += $"  ·  {stats.PromptProgressTokensPerSecond:F1} tok/s";
            return $"Input   {activeInput}\nOutput  waiting for generation…";
        }

        if (stats.PromptTokensPerSecond <= 0 && stats.EvalTokensPerSecond <= 0)
            return "Request stats  Waiting for the first request…";

        var lines = new List<string>();
        var input = stats.PromptTokensPerSecond > 0 ? $"{stats.PromptTokensPerSecond:F1} tok/s" : "not reported";
        if (stats.PromptTokens > 0) input += $"  ·  {stats.PromptTokens:N0} tokens";
        lines.Add($"Input   {input}");

        var output = stats.EvalTokensPerSecond > 0 ? $"{stats.EvalTokensPerSecond:F1} tok/s" : "waiting for generation…";
        if (stats.InitialGenerationTokensPerSecond > 0) output += $"  ·  avg at start: {stats.InitialGenerationTokensPerSecond:F2} tok/s";
        lines.Add($"Output  {output}");

        var details = new List<string>();
        if (stats.GeneratedTokens > 0) details.Add($"{stats.GeneratedTokens:N0} output tokens");
        if (stats.TotalLayers > 0) details.Add($"GPU layers {stats.OffloadedLayers}/{stats.TotalLayers}");
        if (details.Count > 0) lines.Add($"Stats   {string.Join("  ·  ", details)}");
        return string.Join('\n', lines);
    }

}
