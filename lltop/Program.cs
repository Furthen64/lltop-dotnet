using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var app = Application.Create().Init();
var cfg = AppConfig.Load();
var profiles = Profile.LoadAll(cfg.ProfilesDir);
var selected = 0;
var runner = new ServerRunner();
var lines = new ObservableCollection<string>();
var profileItems = new ObservableCollection<string>(profiles.Count == 0
    ? ["(no profiles found)"]
    : profiles.Select(p => p.Name).ToList());

var win = new Window { Title = "lltop", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
var profileList = new ListView { X = 0, Y = 1, Width = Dim.Percent(35), Height = Dim.Fill(2) };
profileList.SetSource(profileItems);
var logView = new TextView { X = Pos.Right(profileList), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true };
var status = new Label { X = 0, Y = Pos.Bottom(profileList), Width = Dim.Fill(), Text = "Loading..." };
var help = new Label { X = 0, Y = Pos.Bottom(status), Width = Dim.Fill(), Text = "↑/↓ Select  Enter Launch  s Stop  k Kill  r Restart  q Quit" };
win.Add(profileList, logView, status, help);

void UpdateStatus(string message = "")
{
    var profile = profiles.Count > 0 ? profiles[Math.Clamp(selected, 0, profiles.Count - 1)] : null;
    var state = runner.Status;
    var detail = profile is null ? "No profiles" : $"{profile.Name}  {profile.Model}";
    status.Text = $"{state}  {detail}" + (string.IsNullOrWhiteSpace(message) ? "" : $"  |  {message}");
}

void RefreshLogs()
{
    logView.Text = string.Join(Environment.NewLine, lines);
    logView.MoveEnd();
    UpdateStatus();
}

async Task Launch(bool restart = false)
{
    if (profiles.Count == 0) { UpdateStatus("No profile to launch"); return; }
    if (runner.IsRunning)
    {
        if (!restart) { UpdateStatus("Already running"); return; }
        await runner.StopAsync();
    }
    lines.Clear();
    RefreshLogs();
    var profile = profiles[selected];
    try
    {
        await runner.StartAsync(cfg, profile, line =>
        {
            Application.Invoke(() => { lines.Add(line); while (lines.Count > 500) lines.RemoveAt(0); RefreshLogs(); });
        });
        UpdateStatus("Launched");
    }
    catch (Exception ex) { UpdateStatus(ex.Message); }
}

app.Keyboard.KeyDown += (_, args) =>
{
    var key = args;
    var text = key.AsGrapheme;
    if (key.KeyCode == KeyCode.CursorUp) { selected = Math.Max(0, selected - 1); profileList.SelectedItem = selected; UpdateStatus(); key.Handled = true; }
    else if (key.KeyCode == KeyCode.CursorDown) { selected = Math.Min(Math.Max(0, profiles.Count - 1), selected + 1); profileList.SelectedItem = selected; UpdateStatus(); key.Handled = true; }
    else if (key.KeyCode == KeyCode.Enter) { _ = Launch(); key.Handled = true; }
    else if (text.Equals("s", StringComparison.OrdinalIgnoreCase)) { _ = runner.StopAsync(); UpdateStatus("Stopping"); key.Handled = true; }
    else if (text.Equals("k", StringComparison.OrdinalIgnoreCase)) { _ = runner.KillAsync(); UpdateStatus("Killed"); key.Handled = true; }
    else if (text.Equals("r", StringComparison.OrdinalIgnoreCase)) { _ = Launch(true); key.Handled = true; }
    else if (text.Equals("q", StringComparison.OrdinalIgnoreCase) || key.KeyCode == KeyCode.Esc) { _ = runner.StopAsync(); Application.RequestStop(); key.Handled = true; }
};

UpdateStatus(cfg.LoadMessage);
app.Run(win);
runner.Dispose();

sealed class AppConfig
{
    public string LlamaServer { get; set; } = "";
    public string ProfilesDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "lltop", "profiles");
    public string LogsDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "lltop", "logs");
    public string DefaultHost { get; set; } = "0.0.0.0";
    public int DefaultPort { get; set; } = 8080;
    public string LoadMessage { get; private set; } = "";

    public static AppConfig Load()
    {
        var c = new AppConfig();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "lltop");
        var path = Path.Combine(root, "config.toml");
        try
        {
            if (File.Exists(path)) Toml.ReadInto(path, c);
            c.ProfilesDir = Expand(c.ProfilesDir); c.LogsDir = Expand(c.LogsDir); c.LlamaServer = Expand(c.LlamaServer);
            Directory.CreateDirectory(c.ProfilesDir); Directory.CreateDirectory(c.LogsDir);
            c.LoadMessage = File.Exists(path) ? $"config: {path}" : $"config missing; using defaults ({path})";
        }
        catch (Exception ex) { c.LoadMessage = $"config error: {ex.Message}"; }
        return c;
    }
    public static string Expand(string value) => string.IsNullOrWhiteSpace(value) ? value : Environment.ExpandEnvironmentVariables(value.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal));
}

sealed class Profile
{
    public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string LlamaServer { get; set; } = ""; public string Model { get; set; } = "";
    public string Host { get; set; } = "0.0.0.0"; public int Port { get; set; } = 8080; public int Ctx { get; set; } = 65536; public int Ngl { get; set; } = 99;
    public string CacheK { get; set; } = "q4_0"; public string CacheV { get; set; } = "q4_0"; public double Temp { get; set; } = .1; public double TopP { get; set; } = .95; public int TopK { get; set; } = 40; public double MinP { get; set; } = .05;
    public int Batch { get; set; } = 512; public int UBatch { get; set; } = 256; public int Parallel { get; set; } = 1; public int Threads { get; set; } = 0; public string FlashAttn { get; set; } = "auto"; public bool Jinja { get; set; } = true; public bool Metrics { get; set; } = true; public bool NoMmap { get; set; } = true; public string ChatTemplate { get; set; } = "chatml"; public string Reasoning { get; set; } = "auto"; public int ReasoningBudget { get; set; } = -1; public List<string> ExtraArgs { get; set; } = [];
    public static List<Profile> LoadAll(string dir)
    {
        var result = new List<Profile>();
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir, "*.toml").OrderBy(x => x))
        {
            try { var p = new Profile(); Toml.ReadInto(path, p); if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Path.GetFileNameWithoutExtension(path); p.Model = AppConfig.Expand(p.Model); p.LlamaServer = AppConfig.Expand(p.LlamaServer); result.Add(p); } catch { }
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

static class Toml
{
    public static void ReadInto<T>(string path, T target)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('#', 2)[0].Trim(); if (line.Length == 0 || !line.Contains('=')) continue;
            var pair = line.Split('=', 2); var key = pair[0].Trim().Replace("-", "_"); var value = pair[1].Trim();
            var prop = typeof(T).GetProperties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)); if (prop is null) continue;
            try { if (prop.PropertyType == typeof(string)) prop.SetValue(target, Unquote(value)); else if (prop.PropertyType == typeof(int)) prop.SetValue(target, int.Parse(value)); else if (prop.PropertyType == typeof(double)) prop.SetValue(target, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); else if (prop.PropertyType == typeof(bool)) prop.SetValue(target, bool.Parse(value)); else if (prop.PropertyType == typeof(List<string>)) prop.SetValue(target, value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Unquote).ToList()); } catch { }
        }
    }
    static string Unquote(string s) => s.Trim().Trim('"', '\'').Trim();
}

sealed class ServerRunner : IDisposable
{
    Process? process; public string Status { get; private set; } = "stopped"; public bool IsRunning => process is { HasExited: false };
    public async Task StartAsync(AppConfig cfg, Profile profile, Action<string> onLine)
    {
        var executable = string.IsNullOrWhiteSpace(profile.LlamaServer) ? cfg.LlamaServer : profile.LlamaServer;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("llama_server path is required");
        if (!File.Exists(executable)) throw new FileNotFoundException("llama_server not found", executable);
        if (string.IsNullOrWhiteSpace(profile.Model) || !File.Exists(profile.Model)) throw new FileNotFoundException("model not found", profile.Model);
        var args = $"-m {Quote(profile.Model)} --host {Quote(profile.Host)} --port {profile.Port} -c {profile.Ctx} -ngl {profile.Ngl} --cache-type-k {profile.CacheK} --cache-type-v {profile.CacheV} --temp {profile.Temp} --top-p {profile.TopP} --top-k {profile.TopK} --min-p {profile.MinP} -b {profile.Batch} -ub {profile.UBatch} --parallel {profile.Parallel} --flash-attn {profile.FlashAttn}";
        if (profile.Threads > 0) args += $" --threads {profile.Threads}"; if (profile.Jinja) args += " --jinja"; if (profile.Metrics) args += " --metrics"; if (profile.NoMmap) args += " --no-mmap"; if (!string.IsNullOrWhiteSpace(profile.ChatTemplate)) args += $" --chat-template {Quote(profile.ChatTemplate)}"; if (!string.IsNullOrWhiteSpace(profile.Reasoning)) args += $" --reasoning {Quote(profile.Reasoning)} --reasoning-budget {profile.ReasoningBudget}"; args += string.Join(' ', profile.ExtraArgs.Select(Quote));
        Directory.CreateDirectory(cfg.LogsDir); var log = Path.Combine(cfg.LogsDir, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{profile.Name}.log");
        process = new Process { StartInfo = new ProcessStartInfo(executable, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } }; process.Start(); Status = "running";
        async Task Read(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) { await File.AppendAllTextAsync(log, line + Environment.NewLine); onLine(line); } }
        _ = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError)).ContinueWith(_ =>
        {
            Status = process.HasExited && process.ExitCode == 0 ? "stopped" : "failed";
        });
    }
    public async Task StopAsync() { if (!IsRunning) return; Status = "stopping"; try { process!.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } catch { } }
    public Task KillAsync() => StopAsync(); public void Dispose() { if (IsRunning) process!.Kill(true); process?.Dispose(); }
    static string Quote(string s) => s.Contains(' ') ? $"\"{s.Replace("\"", "\\\"") }\"" : s;
}
