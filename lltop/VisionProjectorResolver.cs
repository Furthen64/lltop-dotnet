using System.Text;

sealed record VisionProjectorMatch(string? Path, string Message, bool MetadataMatched);

static class VisionProjectorResolver
{
    public const string ExpectedProjectorName = "mmproj-BF16.gguf";
    static readonly string[] IdentityKeys = ["general.name", "general.basename", "general.architecture"];
    static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "gguf", "mmproj", "model", "instruct", "chat", "bf16", "f16", "fp16", "q4", "q5", "q6", "q8", "ud", "xl"
    };

    public static bool SupportsModel(string modelPath)
    {
        var name = Path.GetFileName(modelPath).Replace('_', '-');
        return (name.Contains("qwen3.6", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("35b-a3b", StringComparison.OrdinalIgnoreCase)) ||
               (name.Contains("qwen3.8", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("27b", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsExpectedProjector(string projectorPath) =>
        Path.GetFileName(projectorPath).Equals(ExpectedProjectorName, StringComparison.OrdinalIgnoreCase);

    public static VisionProjectorMatch FindBeside(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return new(null, "Select an existing model GGUF first.", false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(modelPath));
        if (directory is null) return new(null, "The model directory could not be determined.", false);
        var candidates = Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0) return new(null, "No sibling mmproj*.gguf files found.", false);

        GgufMetadata model;
        try { model = GgufMetadataReader.Read(modelPath); }
        catch (Exception ex) { return new(null, $"Could not inspect the model GGUF: {ex.Message}", false); }
        var modelIdentity = Identity(model, modelPath);
        var inspected = new List<(string Path, int Score, bool Identified)>();
        foreach (var candidate in candidates)
        {
            try
            {
                var metadata = GgufMetadataReader.Read(candidate);
                var type = metadata.String("general.type");
                if (!string.IsNullOrWhiteSpace(type) && !type.Equals("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
                var projectorIdentity = Identity(metadata, candidate);
                var overlap = modelIdentity.Intersect(projectorIdentity, StringComparer.Ordinal).Count();
                inspected.Add((candidate, overlap, overlap > 0));
            }
            catch { /* A corrupt/non-GGUF sibling is not a usable projector. */ }
        }
        if (inspected.Count == 0) return new(null, "Sibling mmproj files were found, but none had readable projector metadata.", false);
        var bestScore = inspected.Max(x => x.Score);
        var best = inspected.Where(x => x.Score == bestScore).ToList();
        if (bestScore > 0 && best.Count == 1)
            return new(best[0].Path, $"Matched {Path.GetFileName(best[0].Path)} from GGUF model metadata.", true);
        if (inspected.Count == 1)
            return new(inspected[0].Path, $"Found the only readable sibling, {Path.GetFileName(inspected[0].Path)}; its metadata does not identify the model family.", false);
        return new(null, bestScore > 0
            ? $"{best.Count} projectors match equally; choose one explicitly."
            : $"Found {inspected.Count} readable projectors, but metadata cannot distinguish them.", false);
    }

    static HashSet<string> Identity(GgufMetadata metadata, string path)
    {
        var text = new StringBuilder(Path.GetFileNameWithoutExtension(path));
        foreach (var key in IdentityKeys)
            if (metadata.String(key) is { Length: > 0 } value) text.Append(' ').Append(value);
        foreach (var pair in metadata.Values)
            if (pair.Key.StartsWith("general.base_model.", StringComparison.Ordinal) && pair.Key.EndsWith(".name", StringComparison.Ordinal) && pair.Value is string value)
                text.Append(' ').Append(value);
        var normalized = new string(text.ToString().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var tokens = Tokenize(text.ToString());
        if (normalized.Contains("qwen36")) tokens.Add("qwen36");
        if (normalized.Contains("35ba3b")) tokens.Add("35ba3b");
        return tokens;
    }

    static HashSet<string> Tokenize(string value) => value.ToLowerInvariant()
        .Split(value.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length >= 3 && !Noise.Contains(token) && !token.All(char.IsDigit))
        .ToHashSet(StringComparer.Ordinal);
}
