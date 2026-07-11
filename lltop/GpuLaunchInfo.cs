sealed record GpuLaunchInfo(string Summary, bool IsExplicit)
{
    public static GpuLaunchInfo ForProfile(Profile profile)
    {
        var explicitParts = new List<string>();
        var envParts = new List<string>();

        var devices = ReadMultiValueOption(profile.ExtraArgs, "--device");
        if (devices.Count > 0) explicitParts.Add(devices.Count == 1 ? $"device {devices[0]}" : $"devices {string.Join(", ", devices)}");

        var mainGpu = ReadLastValueOption(profile.ExtraArgs, "--main-gpu", "-mg");
        if (!string.IsNullOrWhiteSpace(mainGpu)) explicitParts.Add($"main GPU {mainGpu}");

        var splitMode = ReadLastValueOption(profile.ExtraArgs, "--split-mode", "-sm");
        if (!string.IsNullOrWhiteSpace(splitMode)) explicitParts.Add($"split {splitMode}");

        var tensorSplit = ReadLastValueOption(profile.ExtraArgs, "--tensor-split", "-ts");
        if (!string.IsNullOrWhiteSpace(tensorSplit)) explicitParts.Add($"tensor split {tensorSplit}");

        foreach (var name in new[] { "CUDA_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "GPU_DEVICE_ORDINAL", "ZE_AFFINITY_MASK" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) envParts.Add($"{name}={value}");
        }

        if (explicitParts.Count > 0 && envParts.Count > 0)
            return new($"{string.Join("  ", explicitParts)}  via {string.Join("  ", envParts)}", true);
        if (explicitParts.Count > 0)
            return new(string.Join("  ", explicitParts), true);
        if (envParts.Count > 0)
            return new($"visible {string.Join("  ", envParts)}", true);
        return new("auto (not pinned by profile or environment)", false);
    }

    static List<string> ReadMultiValueOption(IReadOnlyList<string> args, params string[] names)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (names.Contains(arg, StringComparer.Ordinal))
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-')) values.Add(args[++i]);
                continue;
            }

            foreach (var name in names)
            {
                var prefix = name + "=";
                if (arg.StartsWith(prefix, StringComparison.Ordinal)) values.Add(arg[prefix.Length..]);
            }
        }
        return values;
    }

    static string ReadLastValueOption(IReadOnlyList<string> args, params string[] names)
    {
        var values = ReadMultiValueOption(args, names);
        return values.Count == 0 ? "" : values[^1];
    }
}
