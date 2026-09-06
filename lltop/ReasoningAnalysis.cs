using System.Text.RegularExpressions;

internal static class ReasoningAnalysis
{
    public static IReadOnlyList<string> EffortLevels(GgufMetadata metadata)
    {
        var template = metadata.String("tokenizer.chat_template") ?? "";
        var levels = new HashSet<string>();
        foreach (Match list in Regex.Matches(template,
            @"\b\w*reasoning_effort\w*\s+(?:not\s+)?in\s*[\(\[](?<levels>[^\)\]]+)[\)\]]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            foreach (Match value in Regex.Matches(list.Groups["levels"].Value, "['\"](?<level>minimal|low|medium|high|xhigh|max)['\"]"))
                levels.Add(value.Groups["level"].Value);
        return new[] { "minimal", "low", "medium", "high", "xhigh", "max" }.Where(levels.Contains).ToArray();
    }

    public static string Guidance(GgufMetadata metadata, string effort)
    {
        var levels = EffortLevels(metadata);
        var title = $"{metadata.String("general.name") ?? "Model"} analyzed.";
        if (levels.Count == 0)
            return title + "\nNo effort choices could be confirmed from this template.\nLeave Effort blank for the model default; use mode and budget to control thinking.\nExisting settings have been kept. Technical details explain what was found.";
        var guidance = title + "\nChoose effort above, then Save. Your budget stays unchanged.\n"
            + (levels.Contains("medium") ? "Start with Medium for a balance of speed and depth.\n" : "Choose one of the detected effort levels.\n")
            + "Lower effort favors speed; higher effort favors more thorough reasoning.\n"
            + "Default follows the template; it does not necessarily mean Medium.";
        if (!string.IsNullOrWhiteSpace(effort) && effort != "default" && !levels.Contains(effort))
            guidance += $"\nCurrent effort '{effort}' was not found among the choices. Choose a listed value.";
        return guidance;
    }

    // Inspect template text only; never execute Jinja from a model file.
    public static string Describe(GgufMetadata metadata)
    {
        var name = metadata.String("general.name") ?? "Unknown";
        var architecture = metadata.String("general.architecture") ?? "Unknown";
        var template = metadata.String("tokenizer.chat_template");
        var lines = new List<string> { $"Model: {name}", $"Architecture: {architecture}" };
        if (string.IsNullOrWhiteSpace(template))
        {
            lines.Add("No default chat template found. Reasoning support is unknown.");
            return string.Join('\n', lines);
        }
        lines.Add("Source: tokenizer.chat_template (embedded default)");
        lines.Add(template.Contains("enable_thinking", StringComparison.Ordinal)
            ? "Thinking: template references enable_thinking (on/off control)."
            : "Thinking: no enable_thinking switch found; on/off support is unconfirmed.");
        if (template.Contains("reasoning_effort", StringComparison.Ordinal))
        {
            var levels = new List<string>();
            foreach (Match list in Regex.Matches(template,
                @"\b\w*reasoning_effort\w*\s+(?:not\s+)?in\s*[\(\[](?<levels>[^\)\]]+)[\)\]]",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                foreach (Match value in Regex.Matches(list.Groups["levels"].Value, "['\"](?<level>minimal|low|medium|high|xhigh|max)['\"]"))
                    if (!levels.Contains(value.Groups["level"].Value)) levels.Add(value.Groups["level"].Value);
            lines.Add(levels.Count > 0
                ? $"Effort values listed in template: {string.Join(", ", levels)}."
                : "Effort: reasoning_effort is referenced; accepted levels are not established.");
            if (architecture.Equals("gpt-oss", StringComparison.OrdinalIgnoreCase) && levels.Count == 0)
                lines.Add("Family hint: standard gpt-oss uses low / medium / high.");
        }
        else lines.Add("Effort: no reasoning_effort reference found. Leave Effort blank.");
        lines.Add(template.Contains("preserve_thinking", StringComparison.Ordinal) || template.Contains("clear_thinking", StringComparison.Ordinal)
            ? "History: template includes a thinking-history preservation control."
            : "History: no known thinking-history preservation control found.");
        lines.Add("Text inspection is advisory; it does not prove runtime support.");
        lines.Add("Budget enforcement depends on llama-server and model thinking tags.");
        return string.Join('\n', lines);
    }
}
