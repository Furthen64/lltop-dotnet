using System.Text;

internal static class ArgumentText
{
    public static string Format(IEnumerable<string> args) => string.Join(' ', args.Select(arg => arg.Any(char.IsWhiteSpace) || arg.Contains('"') ? $"\"{arg.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" : arg));

    public static List<string> Parse(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaped = false;
        foreach (var c in text)
        {
            if (escaped) { current.Append(c); escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else current.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(c);
        }
        if (escaped) current.Append('\\');
        if (quote != '\0') throw new FormatException("Extra arguments contain an unmatched quote.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
