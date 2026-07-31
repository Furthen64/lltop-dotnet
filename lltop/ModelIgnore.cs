using System.Text.RegularExpressions;

sealed class ModelIgnore
{
    readonly List<Rule> rules;
    ModelIgnore(List<Rule> rules) => this.rules = rules;

    public static ModelIgnore Load(string modelsRoot)
    {
        var path = Path.Combine(modelsRoot, ".llmignore");
        if (!File.Exists(path)) return new([]);
        var rules = new List<Rule>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var include = line.StartsWith('!');
            if (include) line = line[1..].Trim();
            if (line.Length > 0) rules.Add(new(ToRegex(line), include));
        }
        return new(rules);
    }

    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var path = relativePath.Replace(Path.DirectorySeparatorChar, '/').TrimStart('/');
        var ignored = false;
        foreach (var rule in rules)
            if (rule.Pattern.IsMatch(path + (isDirectory ? "/" : ""))) ignored = !rule.Include;
        return ignored;
    }

    static Regex ToRegex(string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        var directoryOnly = pattern.EndsWith('/');
        pattern = pattern.Trim('/');
        var rooted = pattern.Contains('/');
        var expression = new System.Text.StringBuilder(rooted ? "^" : "(^|.*/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                if (i + 2 < pattern.Length && pattern[i + 2] == '/') { expression.Append("(?:.*/)?"); i += 2; }
                else { expression.Append(".*"); i++; }
            }
            else if (c == '*') expression.Append("[^/]*");
            else if (c == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(c.ToString()));
        }
        expression.Append(directoryOnly ? "(/.*)?$" : "$");
        return new(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    sealed record Rule(Regex Pattern, bool Include);
}
