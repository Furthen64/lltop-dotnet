using System.Globalization;
using System.Text;

// A deliberately simple, append-only source for external graphing tools.  The
// first line describes the tab-separated columns; comment lines are metadata.
internal sealed class RunGraphDataWriter : IDisposable
{
    readonly StreamWriter writer;
    readonly object gate = new();

    private RunGraphDataWriter(string path, StreamWriter writer)
    {
        Path = path;
        this.writer = writer;
    }

    public string Path { get; }

    public static RunGraphDataWriter Create(string directory, Profile profile, DateTimeOffset started)
    {
        Directory.CreateDirectory(directory);
        var stem = $"run-{started:yyyyMMdd-HHmmss}-{ProfileStore.Slugify(profile.Name)}";
        var path = UniquePath(directory, stem);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var result = new RunGraphDataWriter(path, writer);
        result.WriteComment("lltop realtime graph data v1");
        result.WriteComment($"profile: {profile.Name}");
        result.WriteComment($"started_at: {started:O}");
        result.WriteLine("timestamp_utc\tkind\tcpu_percent\tsystem_ram_used_bytes\tsystem_ram_total_bytes\tgpu_percent\tvram_used_bytes\tvram_total_bytes\tlabel");
        result.WriteEvent(started, "run_started", "Run started");
        return result;
    }

    public void WriteSample(RunResourceSample sample) => WriteLine(string.Join('\t',
        Timestamp(sample.Timestamp), "sample", Number(sample.CpuUsagePercent), Number(sample.SystemRamUsedBytes), Number(sample.SystemRamTotalBytes),
        Number(sample.GpuUsagePercent), Number(sample.VramUsedBytes), Number(sample.VramTotalBytes), ""));

    public void WriteEvent(DateTimeOffset timestamp, string kind, string label) => WriteLine(string.Join('\t',
        Timestamp(timestamp), Clean(kind), "", "", "", "", "", "", Clean(label)));

    void WriteComment(string value) => WriteLine("# " + Clean(value));

    void WriteLine(string value)
    {
        lock (gate) writer.WriteLine(value);
    }

    public void Dispose()
    {
        lock (gate) writer.Dispose();
    }

    static string UniquePath(string directory, string stem)
    {
        for (var suffix = 0; ; suffix++)
        {
            var path = System.IO.Path.Combine(directory, suffix == 0 ? stem + ".dat" : $"{stem}-{suffix}.dat");
            if (!File.Exists(path)) return path;
        }
    }

    static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static string Number<T>(T? value) where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture) ?? "";
    static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal static class RunGraphEvents
{
    internal static (string Kind, string Label)? FromLogLine(string line)
    {
        var parsed = LlamaLogParser.Parse(line);
        if (parsed.IsError) return ("error", parsed.ErrorKind.Replace('_', ' ') + ": " + parsed.ErrorMessage);
        if (parsed.Cancelled) return ("cancelled", "Task cancelled");
        if (line.Contains("all slots are idle", StringComparison.OrdinalIgnoreCase)) return ("all_slots_idle", "All slots idle");
        if (line.Contains("new prompt,", StringComparison.OrdinalIgnoreCase)) return ("prompt_started", "Prompt started");
        if (line.Contains("print_timing:", StringComparison.OrdinalIgnoreCase)) return ("request_finished", "Request timing reported");
        if (line.Contains("server is listening", StringComparison.OrdinalIgnoreCase)) return ("server_ready", "Server ready");
        return null;
    }
}
