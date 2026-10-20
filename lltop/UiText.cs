static class UiText
{
    public static string ProfileGlyph(bool isBroken, bool isRunning) =>
        isBroken ? "💥" : isRunning ? "●" : "○";

    public sealed record ProfileRowData(string Marker, bool Vision, string Name, IReadOnlyList<string> Tags, string ModelSize, string MmSize)
    {
        public ProfileRowData(string marker, bool vision, string name, IReadOnlyList<string> tags, string size)
            : this(marker, vision, name, tags, size, "") => LegacySingleSize = true;

        public bool LegacySingleSize { get; init; }
    }

    public static int ProfileListItem(int profileIndex, int profileCount, int favoriteCount) =>
        profileIndex + (HasFavoritesDivider(profileCount, favoriteCount) && profileIndex >= favoriteCount ? 1 : 0);

    public static int SkipFavoritesDivider(int requestedItem, int previousItem, int profileCount, int favoriteCount) =>
        !HasFavoritesDivider(profileCount, favoriteCount) || requestedItem != favoriteCount
            ? requestedItem
            : Math.Clamp(requestedItem + (requestedItem < previousItem ? -1 : 1), 0, profileCount);

    public static string ProfileHeader(int width)
    {
        var columns = ProfileColumnWidths(width);
        return new string(' ', columns.Prefix)
            + MiddleEllipsize("PROFILE", columns.Name).PadRight(columns.Name)
            + new string(' ', columns.Gap) + MiddleEllipsize("TAGS", columns.Tags).PadRight(columns.Tags)
            + new string(' ', columns.Gap) + MiddleEllipsize("MODELSIZE", columns.ModelSize).PadLeft(columns.ModelSize)
            + new string(' ', columns.Gap) + MiddleEllipsize("MMSIZE", columns.MmSize).PadLeft(columns.MmSize);
    }

    static bool HasFavoritesDivider(int profileCount, int favoriteCount) => favoriteCount > 0 && favoriteCount < profileCount;

    // Columns: glyph · [V] slot · profile · tags · model size · projector size.
    public static List<string> ProfileRows(IEnumerable<ProfileRowData> source, int width)
    {
        var lines = new List<string>();
        width = Math.Max(12, width);
        var parts = source.Select(r =>
        {
            var prefix = r.Marker == "💥"
                ? r.Marker + " " + (r.Vision ? "[V]" : "   ")
                : r.Marker + " " + (r.Vision ? "[V] " : "    ");
            return (R: r, Prefix: prefix);
        }).ToList();
        if (parts.Count == 0) return lines;
        if (parts.All(p => p.R.LegacySingleSize)) return LegacyProfileRows(parts.Select(p => p.R), width);
        var columns = ProfileColumnWidths(width);
        foreach (var part in parts)
        {
            var tagText = string.Join(", ", part.R.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
            lines.Add(part.Prefix
                + MiddleEllipsize(part.R.Name, columns.Name).PadRight(columns.Name)
                + new string(' ', columns.Gap) + MiddleEllipsize(tagText, columns.Tags).PadRight(columns.Tags)
                + new string(' ', columns.Gap) + MiddleEllipsize(part.R.ModelSize, columns.ModelSize).PadLeft(columns.ModelSize)
                + new string(' ', columns.Gap) + MiddleEllipsize(part.R.MmSize, columns.MmSize).PadLeft(columns.MmSize));
        }
        return lines;
    }

    static List<string> LegacyProfileRows(IEnumerable<ProfileRowData> source, int width)
    {
        var rows = new List<string>();
        var parts = source.Select(r =>
        {
            var prefix = r.Marker + " " + (r.Vision ? "[V] " : "    ");
            var suffix = string.IsNullOrWhiteSpace(r.ModelSize) ? "" : $" {r.ModelSize}";
            return (R: r, Prefix: prefix, Suffix: suffix, Avail: Math.Max(1, width - prefix.Length - suffix.Length));
        }).ToList();
        var hasTags = parts.Any(p => p.R.Tags.Any(t => !string.IsNullOrWhiteSpace(t)));
        var nameWidth = hasTags
            ? Math.Clamp(parts.Max(p => MiddleEllipsize(p.R.Name, p.Avail).Length), 0, Math.Max(0, parts.Min(p => p.Avail) - 2))
            : 0;
        foreach (var part in parts)
        {
            if (part.Prefix.Length + part.Suffix.Length >= width)
            {
                rows.Add(MiddleEllipsize(part.Prefix + part.R.Name + part.Suffix, width));
                continue;
            }
            if (!hasTags)
            {
                rows.Add(part.Prefix + MiddleEllipsize(part.R.Name, part.Avail).PadRight(part.Avail) + part.Suffix);
                continue;
            }
            var tagText = string.Join(", ", part.R.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
            var middle = MiddleEllipsize(part.R.Name, nameWidth).PadRight(nameWidth);
            if (tagText.Length > 0) middle += "  " + MiddleEllipsize($"({tagText})", Math.Max(0, part.Avail - nameWidth - 2));
            rows.Add(part.Prefix + middle.PadRight(part.Avail) + part.Suffix);
        }
        return rows;
    }

    static (int Prefix, int Gap, int Name, int Tags, int ModelSize, int MmSize) ProfileColumnWidths(int width)
    {
        width = Math.Max(12, width);
        const int prefix = 6;
        var gap = width >= 45 ? 2 : 1;
        var available = Math.Max(0, width - prefix - gap * 3);
        var mmSize = Math.Min(16, Math.Max(6, width / 4));
        var modelSize = 9;
        while (modelSize + mmSize > available && mmSize > 1) mmSize--;
        while (modelSize + mmSize > available && modelSize > 1) modelSize--;
        var flexible = Math.Max(0, available - modelSize - mmSize);
        var name = flexible == 0 ? 0 : Math.Max(1, flexible * 2 / 3);
        var tags = flexible - name;
        return (prefix, gap, name, tags, modelSize, mmSize);
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
            var percent = (int)Math.Round(stats.Progress * 100, MidpointRounding.AwayFromZero);
            var activeInput = $"reading {percent} %";
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
