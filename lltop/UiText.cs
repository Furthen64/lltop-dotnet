static class UiText
{
    public static string ProfileRow(string marker, bool vision, string name, string size, int width)
    {
        var prefix = marker + (vision ? " [V] " : " ");
        var suffix = string.IsNullOrWhiteSpace(size) ? "" : $"  {size}";
        var available = Math.Max(1, width - prefix.Length - suffix.Length);
        if (prefix.Length + suffix.Length >= width)
            return MiddleEllipsize(prefix + name + suffix, width);
        return prefix + MiddleEllipsize(name, available).PadRight(available) + suffix;
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
}
