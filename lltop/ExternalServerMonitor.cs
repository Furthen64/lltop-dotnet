using System.Diagnostics;

internal enum ExternalServerState { Reconnecting, AttachedDegraded, Ready }

internal sealed record ExternalServer(
    int Pid,
    string Command,
    string LogPath,
    string Host,
    int Port,
    string Model,
    ExternalServerState State,
    string StateDetail);

internal sealed record ExternalLogUpdate(ExternalServer? Server, IReadOnlyList<string> Lines);
internal sealed record ExternalServerLaunch(string Host, int Port, string Model);

internal sealed class ExternalServerMonitor(AppConfig config)
{
    readonly HttpClient healthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    string logPath = "";
    long offset;
    int trackedPid;
    int healthFailures;

    public async Task<ExternalLogUpdate> PollAsync(CancellationToken cancellationToken)
    {
        var server = Detect(config.LogsDir);
        if (server is null)
        {
            logPath = "";
            offset = 0;
            trackedPid = 0;
            healthFailures = 0;
            return new(null, []);
        }

        server = await ReconcileAsync(server, cancellationToken);
        return new(server, ReadLogLines(server));
    }

    async Task<ExternalServer> ReconcileAsync(ExternalServer server, CancellationToken cancellationToken)
    {
        if (server.Pid != trackedPid)
        {
            trackedPid = server.Pid;
            healthFailures = 0;
        }

        var health = await CheckHealthAsync(server.Host, server.Port, cancellationToken);
        if (health.Healthy)
        {
            healthFailures = 0;
            return server with { State = ExternalServerState.Ready, StateDetail = $"endpoint verified at {health.Endpoint}" };
        }

        healthFailures++;
        var state = healthFailures <= 2 ? ExternalServerState.Reconnecting : ExternalServerState.AttachedDegraded;
        return server with { State = state, StateDetail = health.Error };
    }

    async Task<(bool Healthy, string Endpoint, string Error)> CheckHealthAsync(string host, int port, CancellationToken cancellationToken)
    {
        var connectHost = host is "0.0.0.0" or "::" ? "127.0.0.1" : host;
        try
        {
            var endpoint = new UriBuilder(Uri.UriSchemeHttp, connectHost, port, "health").Uri;
            using var response = await healthClient.GetAsync(endpoint, cancellationToken);
            return response.IsSuccessStatusCode
                ? (true, endpoint.ToString(), "")
                : (false, endpoint.ToString(), $"health returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return (false, $"http://{connectHost}:{port}/health", $"health unavailable: {ex.Message}");
        }
    }

    IReadOnlyList<string> ReadLogLines(ExternalServer server)
    {
        if (server.LogPath.Length == 0) return [];
        if (server.LogPath != logPath)
        {
            logPath = server.LogPath;
            try { offset = Math.Max(0, new FileInfo(logPath).Length - 256 * 1024); }
            catch { offset = 0; }
        }
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
            return lines;
        }
        catch { return []; }
    }

    internal static ExternalServer? Detect(string logsDirectory)
    {
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var arguments = ReadArguments(process.Id);
                var command = arguments.Count > 0 ? ArgumentText.Format(arguments) : ReadCommand(process.Id);
                var launch = ParseLaunchArguments(arguments);
                var log = ResolveLog(process.Id, logsDirectory);
                return new(process.Id, command, log, launch.Host, launch.Port, launch.Model,
                    ExternalServerState.Reconnecting, "endpoint has not been verified yet");
            }
        }
        return null;
    }

    internal static ExternalServerLaunch ParseLaunchArguments(IReadOnlyList<string> arguments)
    {
        var host = "127.0.0.1";
        var port = 8080;
        var model = "";
        for (var i = 0; i < arguments.Count; i++)
        {
            var value = arguments[i];
            if (value is "--host" && i + 1 < arguments.Count) host = arguments[++i];
            else if (value.StartsWith("--host=", StringComparison.Ordinal)) host = value[7..];
            else if (value is "--port" && i + 1 < arguments.Count && int.TryParse(arguments[++i], out var parsedPort)) port = parsedPort;
            else if (value.StartsWith("--port=", StringComparison.Ordinal) && int.TryParse(value[7..], out var inlinePort)) port = inlinePort;
            else if (value is "-m" or "--model" && i + 1 < arguments.Count) model = arguments[++i];
            else if (value.StartsWith("--model=", StringComparison.Ordinal)) model = value[8..];
        }
        return new(host, Math.Clamp(port, 1, 65535), model);
    }

    static IReadOnlyList<string> ReadArguments(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return File.ReadAllText($"/proc/{pid}/cmdline")
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            }
            catch { }
        }
        return [];
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
