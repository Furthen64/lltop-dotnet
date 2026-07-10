using System.Diagnostics;

internal sealed record ExternalServer(int Pid, string Command, string LogPath);
internal sealed record ExternalLogUpdate(ExternalServer? Server, IReadOnlyList<string> Lines);

internal sealed class ExternalServerMonitor(AppConfig config)
{
    string logPath = "";
    long offset;

    public ExternalLogUpdate Poll()
    {
        var server = Detect(config.LogsDir);
        if (server is null) { logPath = ""; offset = 0; return new(null, []); }
        if (server.LogPath.Length == 0) return new(server, []);
        if (server.LogPath != logPath) { logPath = server.LogPath; offset = Math.Max(0, new FileInfo(logPath).Length - 256 * 1024); }
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > stream.Length) offset = 0;
            stream.Position = offset;
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            offset = stream.Position;
            if (lines.Count > 200) lines.RemoveRange(0, lines.Count - 200);
            return new(server, lines);
        }
        catch { return new(server, []); }
    }

    internal static ExternalServer? Detect(string logsDirectory)
    {
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var command = ReadCommand(process.Id);
                var log = ResolveLog(process.Id, logsDirectory);
                return new(process.Id, command, log);
            }
        }
        return null;
    }

    static string ReadCommand(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { }
        }
        return OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
    }

    static string ResolveLog(int pid, string logsDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var fd in new[] { 1, 2 })
            {
                try
                {
                    var target = new FileInfo($"/proc/{pid}/fd/{fd}").LinkTarget;
                    if (!string.IsNullOrWhiteSpace(target) && Path.IsPathRooted(target) && File.Exists(target)) return target;
                }
                catch { }
            }
        }
        try { return Directory.EnumerateFiles(logsDirectory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? ""; }
        catch { return ""; }
    }
}
