using System.Globalization;
using System.Text;

static class UiText
{
    public static string ProfileRow(string marker, bool vision, string name, string size, int width, string badges = "")
    {
        var prefix = string.IsNullOrWhiteSpace(badges)
            ? marker + (vision ? " [V] " : " ")
            : marker + " " + badges + (vision ? " [V] " : " ");
        var suffix = string.IsNullOrWhiteSpace(size) ? "" : $"  {size}";
        var available = Math.Max(1, width - DisplayWidth(prefix) - DisplayWidth(suffix));
        if (DisplayWidth(prefix) + DisplayWidth(suffix) >= width)
            return MiddleEllipsizeToWidth(prefix + name + suffix, width);
        return prefix + PadToWidth(MiddleEllipsizeToWidth(name, available), available) + suffix;
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

    static string PadToWidth(string value, int width) => value + new string(' ', Math.Max(0, width - DisplayWidth(value)));

    static string MiddleEllipsizeToWidth(string value, int width)
    {
        if (width <= 0) return "";
        if (DisplayWidth(value) <= width) return value;
        if (width == 1) return "…";
        var content = width - 1;
        var left = (content + 1) / 2;
        return TakeWidth(value, left) + "…" + TakeLastWidth(value, content - left);
    }

    static string TakeWidth(string value, int width)
    {
        var result = new StringBuilder(); var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var size = RuneWidth(rune);
            if (used + size > width) break;
            result.Append(rune); used += size;
        }
        return result.ToString();
    }

    static string TakeLastWidth(string value, int width)
    {
        var runes = value.EnumerateRunes().ToList();
        var result = new List<Rune>(); var used = 0;
        for (var i = runes.Count - 1; i >= 0; i--)
        {
            var size = RuneWidth(runes[i]);
            if (used + size > width) break;
            result.Add(runes[i]); used += size;
        }
        result.Reverse();
        return string.Concat(result);
    }

    static int DisplayWidth(string value) => value.EnumerateRunes().Sum(RuneWidth);

    static int RuneWidth(Rune rune)
    {
        var value = rune.Value;
        if (value is 0xFE0E or 0xFE0F || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return 0;
        return value is >= 0x1100 and <= 0x115F or 0x2329 or 0x232A or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE19 or >= 0xFE30 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF ? 2 : 1;
    }
}
