using System.Collections.ObjectModel;
using System.Globalization;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

using var app = Application.Create().Init();
var cfg = AppConfig.Load();
if (cfg.IsFirstRun && !RunFirstRunWizard(app, cfg)) return;

var store = new ProfileStore(cfg.ProfilesDir);
var removedLegacyStarter = FirstRunProfiles.RemoveLegacyStarter(cfg);
var load = store.LoadAll();
var profiles = load.Profiles;
var selected = Math.Max(0, profiles.FindIndex(p => p.Name.Equals(cfg.DefaultProfile, StringComparison.OrdinalIgnoreCase)));
var runner = new ServerRunner();
var capabilityCache = new ServerCapabilityCache(Path.Combine(Path.GetDirectoryName(AppConfig.ConfigPath) ?? cfg.LogsDir, "server-capabilities.json"));
var runningProfile = "";
Profile? activeProfile = null;
var serverStats = new ServerStats();
var activeRunGate = new object();
var logLines = new List<string>();
var profileItems = new ObservableCollection<string>();
var historySummaries = new Dictionary<string, ProfileRunSummary>(StringComparer.OrdinalIgnoreCase);
var closing = false;
var logAutoScroll = true;
var logScrollRow = 0;
var expandedHelp = false;
var externalMonitor = new ExternalServerMonitor(cfg);
ExternalServer? externalServer = null;
var resourceGpuBackend = "";
var resourceGpuName = "";
using var monitorCancellation = new CancellationTokenSource();
_ = capabilityCache.Get(cfg.LlamaServer);

var win = new Window { Title = " lltop · llama.cpp control center ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
var banner = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Text = "LLAMA SERVER  •  profiles, launches, and live output" };
var profileFrame = new FrameView { Title = " Profiles ", X = 0, Y = 2, Width = Dim.Percent(34), Height = Dim.Fill(13) };
var logFrame = new FrameView { Title = " Profile overview ", X = Pos.Right(profileFrame), Y = 2, Width = Dim.Fill(), Height = Dim.Fill(13) };
var profileList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
var logView = new LogTextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = false, Text = "Waiting for a server launch…" };
profileFrame.Add(profileList); logFrame.Add(logView);
var statusFrame = new FrameView { Title = " Selected profile / server ", X = 0, Y = Pos.Bottom(profileFrame), Width = Dim.Fill(), Height = 10 };
var status = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(), Text = "Loading…" };
statusFrame.Add(status);
var help = new Label { X = 1, Y = Pos.Bottom(statusFrame), Width = Dim.Fill(2), Height = 3,
    Text = "[Enter] Start   [e] Edit   [p] Preview   [n] New   [Ctrl+R/F5] Find models\n[↑/↓] Select   [s] Stop   [H] History   [h/?] All keys   [q] Quit" };
var resourceStrip = new ResourceStripView { X = 1, Y = Pos.Bottom(help), Width = Dim.Fill(2) };
win.Add(banner, profileFrame, logFrame, statusFrame, help, resourceStrip);
LltopTheme.Apply([profileFrame, logFrame, statusFrame], banner, profileList, logView, status, help);

ISystemResourceProvider resourceProvider = OperatingSystem.IsLinux()
    ? new LinuxSystemResourceProvider(
        () => (resourceGpuBackend, resourceGpuName),
        () => runner.State == RunnerState.Running || externalServer is not null ? 1 : 0)
    : new UnavailableSystemResourceProvider(() => runner.State == RunnerState.Running || externalServer is not null ? 1 : 0);

void ApplyLayout()
{
    var helpHeight = expandedHelp ? 6 : 2;
    help.Height = helpHeight;
    help.Text = expandedHelp
        ? "NAVIGATION  [↑/↓] Select   [Enter] Start   [q/Esc] Quit\nSERVER      [s] Stop   [K] Force stop   [r] Restart   [p] Preview   [c] Copy command\nPROFILES    [n] New   [e] Edit   [d] Duplicate   [x] Delete   [Ctrl+R/F5] Find models\nLOG & RUNS  [l] Auto-scroll   [PgUp/PgDn] Scroll   [Home/End] Jump   [H] History\nSTATUS      [▶] Running   [◐] Starting/stopping   [!] Last run failed   [V] Vision\nHELP        [h/?] Show fewer keys"
        : "[Enter] Start   [e] Edit   [p] Preview   [n] New   [Ctrl+R/F5] Find models\n[↑/↓] Select   [s] Stop   [H] History   [h/?] All keys   [q] Quit";
    var reserved = 10 + helpHeight + 1;
    if (win.Viewport.Width is > 0 and < 84)
    {
        var profileHeight = Math.Max(6, (win.Viewport.Height - reserved - 2) / 3);
        profileFrame.X = 0; profileFrame.Y = 2; profileFrame.Width = Dim.Fill(); profileFrame.Height = profileHeight;
        logFrame.X = 0; logFrame.Y = Pos.Bottom(profileFrame); logFrame.Width = Dim.Fill(); logFrame.Height = Dim.Fill(reserved);
    }
    else
    {
        profileFrame.X = 0; profileFrame.Y = 2; profileFrame.Width = Dim.Percent(34); profileFrame.Height = Dim.Fill(reserved);
        logFrame.X = Pos.Right(profileFrame); logFrame.Y = 2; logFrame.Width = Dim.Fill(); logFrame.Height = Dim.Fill(reserved);
    }
    statusFrame.Y = Pos.Bottom(logFrame);
    help.Y = Pos.Bottom(statusFrame);
    resourceStrip.Y = Pos.Bottom(help);
}
win.ViewportChanged += (_, _) => { ApplyLayout(); RefreshProfileItems(runningProfile); };
ApplyLayout();

void RefreshProfileItems(string? selectName = null)
{
    profileItems.Clear();
    if (profiles.Count == 0) profileItems.Add("  No profiles yet — press n to create one");
    else foreach (var p in profiles)
    {
        var summary = SummaryFor(p.Name);
        var marker = p.Name.Equals(runningProfile, StringComparison.OrdinalIgnoreCase)
            ? runner.State == RunnerState.Running ? "▶" : "◐"
            : !File.Exists(AppConfig.Expand(p.Model)) ? "✗"
            : summary?.LastExitCode is not null and not 0 ? "!"
            : summary?.RunCount == 0 ? "✦" : "○";
        var size = CompactModelSize(p.Model);
        var width = Math.Max(12, profileFrame.Viewport.Width > 0 ? profileFrame.Viewport.Width - 3 : 32);
        profileItems.Add(UiText.ProfileRow(marker, p.Vision, p.Name, size, width));
    }
    profileList.SetSource(profileItems);
    if (profiles.Count == 0) { selected = 0; profileList.SelectedItem = 0; }
    else
    {
        var match = selectName is null ? -1 : profiles.FindIndex(p => p.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase));
        selected = Math.Clamp(match >= 0 ? match : selected, 0, profiles.Count - 1);
        profileList.SelectedItem = selected;
    }
}

Profile? SelectedProfile() => profiles.Count == 0 ? null : profiles[Math.Clamp(selected, 0, profiles.Count - 1)];

ProfileRunSummary? SummaryFor(string profileName)
{
    try
    {
        if (!historySummaries.TryGetValue(profileName, out var summary))
            historySummaries[profileName] = summary = RunHistory.Summarize(cfg.RunsDir, profileName);
        return summary;
    }
    catch { return null; }
}

void UpdateStatus(string message = "")
{
    var p = SelectedProfile();
    var state = runner.IsActive ? runner.State.ToString().ToUpperInvariant() : externalServer is null ? runner.State.ToString().ToUpperInvariant() : "EXTERNAL";
    var pidValue = runner.ProcessId ?? externalServer?.Pid;
    var pid = pidValue is int id ? $"  PID {id}" : "";
    var uptime = runner.StartedAt is { } started && runner.IsActive ? $"  Uptime {(DateTimeOffset.Now - started):hh\\:mm\\:ss}" : "";
    if (p is null)
    {
        resourceGpuBackend = "";
        resourceGpuName = "";
        status.Text = $"STATE    {state}{pid}\n\nNo profiles found in {cfg.ProfilesDir}\n{message}";
        return;
    }
    var model = string.IsNullOrWhiteSpace(p.Model) ? "not configured" : Path.GetFileName(p.Model);
    var modelSize = ModelSize(p.Model);
    var gpu = GpuLaunchInfo.ForProfile(p);
    var capability = CapabilitiesFor(p);
    resourceGpuBackend = capability.Backend;
    resourceGpuName = capability.GpuName;
    var plan = LaunchPlanFor(p, capability);
    var summary = SummaryFor(p.Name);
    var backend = string.IsNullOrWhiteSpace(capability.Backend) ? "unknown" : capability.Backend;
    var device = gpu.IsExplicit ? gpu.Summary : "Automatic";
    var runtimeName = Path.GetFileName(capability.BinaryPath);
    var server = $"{runtimeName}  ·  {backend} backend  ·  llama.cpp {capability.BuildSummary}";
    if (!string.IsNullOrWhiteSpace(capability.GpuName)) server += $"  ·  {capability.GpuName}";
    var vision = p.Vision ? $"On  ·  {Path.GetFileName(p.Mmproj)}" : "Off";
    var lastRun = summary?.LastRunAt is { } last
        ? $"{(summary.LastExitCode == 0 ? "Success" : $"Failed (exit {summary.LastExitCode})")}  ·  {UiText.RelativeTime(last, DateTimeOffset.Now)}" +
          (summary.Generation.Latest > 0 ? $"  ·  {summary.Generation.Latest:F1} tok/s" : "")
        : "No runs recorded";
    var lines = new List<string>
    {
        $"{state}{pid}{uptime}  ·  {p.Name}  ·  {p.Host}:{p.Port}",
        $"Model     {model}{(modelSize.Length == 0 ? "" : $"  ·  {modelSize}")}",
        $"Launch    ctx {p.Ctx:N0}  ·  GPU layers {p.Ngl}  ·  parallel {p.Parallel}  ·  FA {p.FlashAttn}",
        $"Vision    {vision}",
        $"Device    {device}",
        $"Server    {server}",
        $"Last run  {lastRun}"
    };
    var notice = serverStats.LastError.Length > 0 ? $"Error     {serverStats.LastError}"
        : serverStats.LastHint.Length > 0 ? $"Hint      {serverStats.LastHint}"
        : plan.RemovedArguments.Count > 0 ? $"Warning   Unsupported options removed: {string.Join(", ", plan.RemovedArguments.Select(x => x.OptionName).Distinct(StringComparer.Ordinal))}"
        : !string.IsNullOrWhiteSpace(message) ? $"Info      {message}"
        : runner.IsActive ? $"Metrics   prompt {serverStats.PromptTokensPerSecond:F1}  ·  gen {serverStats.EvalTokensPerSecond:F1} tok/s  ·  tokens {serverStats.GeneratedTokens}  ·  offload {serverStats.OffloadedLayers}/{serverStats.TotalLayers}"
        : "";
    lines.Add(notice);
    status.Text = string.Join('\n', lines);
}

void RefreshLogs()
{
    var showingLogs = runner.IsActive || externalServer is not null || logLines.Count > 0;
    logFrame.Title = showingLogs ? " Live log " : " Profile overview ";
    logView.Text = showingLogs
        ? logLines.Count == 0 ? "Starting server; waiting for output…" : string.Join('\n', logLines)
        : FormatProfileOverview(SelectedProfile());
    if (logAutoScroll) logView.MoveEnd();
    else logView.ScrollTo(new System.Drawing.Point(0, Math.Clamp(logScrollRow, 0, Math.Max(0, logLines.Count - 1))));
}

string FormatProfileOverview(Profile? profile)
{
    if (profile is null) return "No profile selected.\n\nPress n to create one or F5 to find local models.";
    var name = Path.GetFileName(profile.Model);
    var size = ModelSize(profile.Model);
    var architecture = "unknown";
    var metadataName = "";
    try
    {
        var metadata = GgufMetadataReader.Read(profile.Model);
        architecture = metadata.String("general.architecture") ?? architecture;
        metadataName = metadata.String("general.name") ?? "";
    }
    catch { }
    var vision = profile.Vision
        ? $"Enabled\nProjector     {Path.GetFileName(profile.Mmproj)}"
        : "Disabled";
    var capability = CapabilitiesFor(profile);
    var runtimePath = string.IsNullOrWhiteSpace(capability.BinaryPath)
        ? (string.IsNullOrWhiteSpace(profile.LlamaServer) ? cfg.LlamaServer : profile.LlamaServer)
        : capability.BinaryPath;
    var runtime = Path.GetFileName(runtimePath);
    var backend = string.IsNullOrWhiteSpace(capability.Backend) ? "unknown" : capability.Backend;
    return $"Ready to launch\n\n" +
            $"Model\n" +
           $"File          {name}\n" +
           $"Size          {(size.Length == 0 ? "unavailable" : size)}\n" +
           $"Architecture  {architecture}\n" +
           (metadataName.Length == 0 ? "" : $"Identity      {metadataName}\n") +
           $"Context       {profile.Ctx:N0}\n" +
           $"GPU layers    {profile.Ngl}\n\n" +
           $"Runtime\n" +
           $"File          {runtime}\n" +
           $"Version       llama.cpp {capability.BuildSummary}\n" +
           $"Backend       {backend}\n" +
           $"Path          {runtimePath}\n\n" +
           $"Vision        {vision}";
}

void SaveActiveRun(ServerExit exit)
{
    Profile? profile;
    ServerStats stats;
    lock (activeRunGate)
    {
        profile = activeProfile;
        if (profile is null) return;
        activeProfile = null;
        stats = serverStats;
    }
    if (runner.StartedAt is not { } started) return;
    RunHistory.Save(cfg.RunsDir, RunRecord.Create(profile, runner.Command, started, DateTimeOffset.Now, exit.ExitCode, exit.Requested ? "stopped" : "exit", stats, runner.LogPath));
    historySummaries.Remove(profile.Name);
}

void ReloadProfiles(string? selectName = null, string message = "Profiles reloaded.")
{
    var result = store.LoadAll();
    profiles = result.Profiles;
    historySummaries.Clear();
    RefreshProfileItems(selectName);
    RefreshLogs();
    var suffix = result.Errors.Count == 0 ? message : $"{message}  Skipped: {string.Join(" | ", result.Errors)}";
    UpdateStatus(suffix);
}

void RefreshModels()
{
    try
    {
        var result = FirstRunProfiles.ScanAndGenerate(cfg, capabilityCache.Get(cfg.LlamaServer));
        if (result.ModelsFound == 0)
        {
            ReloadProfiles(message: $"No compatible models found in {cfg.ModelsDir}.");
            MessageBox.Query(app, "Model discovery", $"No compatible GGUF or BIN models were found in:\n{cfg.ModelsDir}\n\nlltop scans up to {FirstRunProfiles.ModelSearchDepth} folders deep and skips unreadable files, mmproj files, and paths matched by .llmignore.", "OK");
            return;
        }
        var message = result.ProfilesCreated == 0
            ? $"Found {result.ModelsFound} models; all already have profiles."
            : $"Refresh complete: found {result.ModelsFound} models, created {result.ProfilesCreated} profiles.";
        ReloadProfiles(message: message);
        var detail = result.ProfilesCreated == 0
            ? $"Found {result.ModelsFound} compatible model{(result.ModelsFound == 1 ? "" : "s")} in:\n{cfg.ModelsDir}\n\nAll of them already have profiles, so nothing was created."
            : $"Found {result.ModelsFound} compatible model{(result.ModelsFound == 1 ? "" : "s")} in:\n{cfg.ModelsDir}\n\nCreated {result.ProfilesCreated} new profile{(result.ProfilesCreated == 1 ? "" : "s")}.";
        MessageBox.Query(app, "Model discovery", detail, "OK");
    }
    catch (Exception ex) { UpdateStatus($"Refresh failed: {ex.Message}"); }
}

async Task Launch(bool restart = false)
{
    var profile = SelectedProfile();
    if (profile is null) { UpdateStatus("Create a profile first (n)."); return; }
    if (!File.Exists(AppConfig.Expand(profile.Model)))
    {
        if (MessageBox.Query(app, "Model not found", $"Model file not found:\n{AppConfig.Expand(profile.Model)}\n\nDelete this profile?", "Cancel", "Delete") == 1)
            DeleteSelected();
        return;
    }
    try
    {
        if (runner.IsActive)
        {
            if (!restart) { UpdateStatus("A server is already active. Use r to restart it."); return; }
            if (cfg.ConfirmRestart && MessageBox.Query(app, "Restart server", "Restart the current server?", "Cancel", "Restart") != 1) return;
            UpdateStatus("Stopping the current server for restart…");
            await runner.StopAsync();
            if (runner.LastExit is { } stopped) SaveActiveRun(stopped);
        }
        if (!restart && !RunHistory.HasRunForScenario(cfg.RunsDir, profile))
        {
            var firstLaunch = ShowFirstLaunchAdvisor(app, cfg, profile, CapabilitiesFor(profile), capabilityCache.Get(cfg.LlamaServer));
            if (firstLaunch == FirstLaunchAction.Cancel) return;
            if (firstLaunch == FirstLaunchAction.Edit)
            {
                EditSelected();
                return;
            }
            if (firstLaunch == FirstLaunchAction.Preview)
            {
                UpdateStatus(FormatPlanSummary(LaunchPlanFor(profile, CapabilitiesFor(profile))));
                return;
            }
        }
        if (!restart && cfg.ConfirmRecentFailure)
        {
            var recent = RunHistory.FindRecentFailure(cfg.RunsDir, profile, cfg.RecentFailureWindowSeconds, cfg.StartupFailureSeconds);
            while (recent is not null)
            {
                var answer = MessageBox.Query(app, "Recent startup failure", $"This configuration failed recently (exit {recent.ExitCode}, {recent.DurationSeconds:F1}s).\n\nAnalyze it before trying again?", "Cancel", "Analyze", "Run again");
                if (answer == 0) return;
                if (answer == 2) break;
                ShowStartupFailureAnalysis(app, cfg, profile, recent);
            }
        }
        var capability = CapabilitiesFor(profile);
        var plan = LaunchPlanFor(profile, capability);
        if (plan.HasManualRemovals)
        {
            var removed = string.Join('\n', plan.RemovedArguments.Where(x => x.FromManualArgs).Select(x => x.Display).Distinct(StringComparer.Ordinal));
            if (MessageBox.Query(app, "Unsupported raw arguments", $"The configured llama-server does not support:\n\n{removed}\n\nLaunch after filtering them out?", "Cancel", "Launch filtered") != 1)
            {
                UpdateStatus("Launch cancelled because unsupported raw arguments were removed.");
                return;
            }
        }
        logLines.Clear(); RefreshLogs();
        lock (activeRunGate) { serverStats = new ServerStats(); activeProfile = profile.Copy(profile.Name); }
        runningProfile = profile.Name;
        await runner.StartAsync(plan, profile, cfg);
        RefreshProfileItems(profile.Name);
        UpdateStatus(plan.RemovedArguments.Count == 0
            ? $"Started successfully. Log: {runner.LogPath}"
            : $"Started with compatibility filtering. Removed: {string.Join(", ", plan.RemovedArguments.Select(x => x.OptionName).Distinct(StringComparer.Ordinal))}. Log: {runner.LogPath}");
    }
    catch (Exception ex)
    {
        runningProfile = ""; RefreshProfileItems(profile.Name); UpdateStatus(ex.Message);
    }
}

async Task Stop(bool force)
{
    if (!runner.IsActive) { UpdateStatus("No managed server is running."); return; }
    UpdateStatus(force ? "Force-stopping the server…" : "Sending interrupt to llama-server…");
    if (force) await runner.KillAsync(); else await runner.StopAsync();
}

void NewProfile()
{
    var p = Profile.CreateDefault(cfg, store.UniqueName("new-profile"));
    p.Description = "New llama-server profile";
    if (!EditProfile(app, p, "Create profile")) return;
    try { store.Save(p); ReloadProfiles(p.Name, $"Created profile {p.Name}."); }
    catch (Exception ex) { UpdateStatus(ex.Message); }
}

void EditSelected()
{
    var p = SelectedProfile(); if (p is null) { UpdateStatus("No profile selected."); return; }
    var edited = p.Copy(p.Name);
    if (!EditProfile(app, edited, "Edit profile")) return;
    try { store.Save(edited); ReloadProfiles(edited.Name, $"Saved profile {edited.Name}."); }
    catch (Exception ex) { UpdateStatus(ex.Message); }
}

void DuplicateSelected()
{
    var source = SelectedProfile(); if (source is null) { UpdateStatus("No profile selected."); return; }
    var copy = source.Copy(store.UniqueName(source.Name + "-copy"));
    try { store.Save(copy); ReloadProfiles(copy.Name, $"Duplicated as {copy.Name}."); }
    catch (Exception ex) { UpdateStatus(ex.Message); }
}

void DeleteSelected()
{
    var p = SelectedProfile(); if (p is null) { UpdateStatus("No profile selected."); return; }
    if (p.Name.Equals(runningProfile, StringComparison.OrdinalIgnoreCase) && runner.IsActive) { UpdateStatus("Stop this profile before deleting it."); return; }
    var answer = MessageBox.Query(app, "Delete profile", $"Delete '{p.Name}'?\n\n{p.SourcePath}", "Cancel", "Delete");
    if (answer != 1) return;
    try { store.Delete(p); ReloadProfiles(message: $"Deleted profile {p.Name}."); }
    catch (Exception ex) { UpdateStatus(ex.Message); }
}

async Task Quit()
{
    if (closing) return;
    if (runner.IsActive)
    {
        var answer = MessageBox.Query(app, "Server is running", "Stop llama-server and quit lltop?", "Cancel", "Stop and quit");
        if (answer != 1) return;
    }
    closing = true;
    monitorCancellation.Cancel();
    await runner.StopAsync();
    app.RequestStop();
}

runner.LineReceived += line => app.Invoke(() =>
{
    serverStats.Consume(line, runner.StartedAt);
    logLines.Add(line);
    if (logLines.Count > 500) logLines.RemoveAt(0);
    RefreshLogs(); UpdateStatus();
});
runner.StateChanged += _ => app.Invoke(() => { RefreshProfileItems(runningProfile); RefreshLogs(); UpdateStatus(); });
runner.Exited += exit => app.Invoke(() =>
{
    var name = runningProfile; runningProfile = "";
    logLines.Clear();
    try { SaveActiveRun(exit); }
    catch (Exception ex) { UpdateStatus($"Could not save run history: {ex.Message}"); }
    RefreshProfileItems(name); RefreshLogs();
    UpdateStatus(exit.Requested ? $"Server stopped (exit {exit.ExitCode})." : $"Server exited with code {exit.ExitCode}." + (exit.Error is null ? "" : $" {exit.Error}"));
});
profileList.ValueChanged += (_, _) =>
{
    if (profileList.SelectedItem is int value && profiles.Count > 0) selected = Math.Clamp(value, 0, profiles.Count - 1);
    RefreshLogs(); UpdateStatus();
};

app.Keyboard.KeyDown += (_, key) =>
{
    if (!ReferenceEquals(app.TopRunnableView, win)) return;
    var text = key.AsGrapheme;
    if (!logAutoScroll && key.KeyCode is KeyCode.PageUp or KeyCode.PageDown or KeyCode.Home or KeyCode.End)
    {
        logScrollRow = key.KeyCode switch
        {
            KeyCode.PageUp => Math.Max(0, logScrollRow - 10),
            KeyCode.PageDown => Math.Min(Math.Max(0, logLines.Count - 1), logScrollRow + 10),
            KeyCode.Home => 0,
            _ => Math.Max(0, logLines.Count - 1)
        };
        logView.ScrollTo(new System.Drawing.Point(0, logScrollRow)); UpdateStatus($"Log row {logScrollRow + 1}/{Math.Max(1, logLines.Count)}."); key.Handled = true;
    }
    else if (key.KeyCode == KeyCode.Enter) { _ = Launch(); key.Handled = true; }
    else if (text == "s") { _ = Stop(false); key.Handled = true; }
    else if (text == "K") { _ = Stop(true); key.Handled = true; }
    else if (key.KeyCode == (KeyCode.R | KeyCode.CtrlMask)) { RefreshModels(); key.Handled = true; }
    else if (text.Equals("r", StringComparison.OrdinalIgnoreCase)) { _ = Launch(true); key.Handled = true; }
    else if (text == "n") { NewProfile(); key.Handled = true; }
    else if (text.Equals("e", StringComparison.OrdinalIgnoreCase)) { EditSelected(); key.Handled = true; }
    else if (text.Equals("d", StringComparison.OrdinalIgnoreCase)) { DuplicateSelected(); key.Handled = true; }
    else if (text.Equals("x", StringComparison.OrdinalIgnoreCase)) { DeleteSelected(); key.Handled = true; }
    else if (text.Equals("p", StringComparison.OrdinalIgnoreCase))
    {
        var p = SelectedProfile();
        UpdateStatus(p is null ? "No profile selected." : FormatPlanSummary(LaunchPlanFor(p, CapabilitiesFor(p))));
        key.Handled = true;
    }
    else if (text.Equals("c", StringComparison.OrdinalIgnoreCase))
    {
        var command = (runner.IsActive ? runner.Command : externalServer?.Command ?? (SelectedProfile() is { } p ? FormatPlanCommand(LaunchPlanFor(p, CapabilitiesFor(p))) : "")) ?? "";
        UpdateStatus(command.Length > 0 && app.Clipboard?.TrySetClipboardData(command) == true ? "Copied launch command to clipboard." : "Clipboard is unavailable.");
        key.Handled = true;
    }
    else if (text.Equals("l", StringComparison.OrdinalIgnoreCase))
    {
        logAutoScroll = !logAutoScroll;
        logScrollRow = Math.Max(0, logLines.Count - 1);
        if (logAutoScroll) logView.MoveEnd(); else logView.ScrollTo(new System.Drawing.Point(0, logScrollRow));
        UpdateStatus($"Log auto-scroll = {logAutoScroll}."); key.Handled = true;
    }
    else if (text == "H")
    {
        var p = SelectedProfile();
        if (p is null) UpdateStatus("No profile selected."); else ShowHistory(app, cfg, p);
        profileList.SetFocus(); UpdateStatus(); key.Handled = true;
    }
    else if (text is "h" or "?") { expandedHelp = !expandedHelp; ApplyLayout(); key.Handled = true; }
    else if (key.KeyCode == KeyCode.F5) { RefreshModels(); key.Handled = true; }
    else if (text.Equals("q", StringComparison.OrdinalIgnoreCase) || key.KeyCode == KeyCode.Esc) { _ = Quit(); key.Handled = true; }
};

RefreshLogs();
var startupMessage = removedLegacyStarter ? "Removed the obsolete empty starter profile." : cfg.LoadMessage;
UpdateStatus(load.Errors.Count == 0 ? startupMessage : $"Skipped invalid profiles: {string.Join(" | ", load.Errors)}");
_ = Task.Run(async () =>
{
    while (!monitorCancellation.IsCancellationRequested)
    {
        try
        {
            var update = externalMonitor.Poll();
            app.Invoke(() =>
            {
                if (runner.IsActive) return;
                var changed = externalServer?.Pid != update.Server?.Pid;
                externalServer = update.Server;
                foreach (var line in update.Lines)
                {
                    serverStats.Consume(line);
                    logLines.Add(line);
                    if (logLines.Count > 500) logLines.RemoveAt(0);
                }
                if (changed || update.Lines.Count > 0) { RefreshLogs(); UpdateStatus(externalServer is null ? "No external server detected." : $"Following external server log: {externalServer.LogPath}"); }
            });
            await Task.Delay(1000, monitorCancellation.Token);
        }
        catch (OperationCanceledException) { break; }
        catch { await Task.Delay(2000); }
    }
});
_ = Task.Run(async () =>
{
    while (!monitorCancellation.IsCancellationRequested)
    {
        try
        {
            var snapshot = await resourceProvider.GetSnapshotAsync(monitorCancellation.Token);
            app.Invoke(() => resourceStrip.Snapshot = snapshot);
            await Task.Delay(TimeSpan.FromSeconds(2), monitorCancellation.Token);
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { break; }
        catch
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), monitorCancellation.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
});
_ = Task.Run(async () => { await Task.Delay(50); app.Invoke(() => RefreshProfileItems(runningProfile)); });
app.Run(win);
monitorCancellation.Cancel();
runner.Dispose();

static bool EditProfile(IApplication app, Profile profile, string title)
{
    var dialog = new Window { Title = $" {title} ", Width = 96, Height = 54 };
    var fields = new Dictionary<string, TextField>();
    TextField Field(string label, string value, int y, int x = 2, int width = 42)
    {
        dialog.Add(new Label { X = x, Y = y, Text = label });
        var field = new TextField { X = x, Y = y + 1, Width = width, Text = value };
        dialog.Add(field); fields[label] = field; return field;
    }
    string T(string label) => fields[label].Text;
    var name = Field("Name", profile.Name, 1);
    Field("Description", profile.Description, 1, 49, 43);
    Field("Model path", profile.Model, 4, 2, 90);
    var mmproj = Field("Vision projector (mmproj)", profile.Mmproj, 7, 2, 72);
    var findMmproj = new Button { X = 76, Y = 8, Text = "Find sibling" };
    dialog.Add(findMmproj);
    Field("llama-server override (blank = global)", profile.LlamaServer, 10, 2, 90);
    Field("Host", profile.Host, 13); Field("Port", profile.Port.ToString(), 13, 49);
    Field("Context", profile.Ctx.ToString(), 16); Field("GPU layers", profile.Ngl.ToString(), 16, 49);
    Field("Parallel", profile.Parallel.ToString(), 19); Field("Threads (0 = auto)", profile.Threads.ToString(), 19, 49);
    Field("Flash attention (auto/on/off)", profile.FlashAttn, 22); Field("Alias", profile.Alias, 22, 49);
    Field("Cache K (q4_0/q8_0/f16/blank)", profile.CacheK, 25); Field("Cache V (q4_0/q8_0/f16/blank)", profile.CacheV, 25, 49);
    Field("Temperature", profile.Temp.ToString(CultureInfo.InvariantCulture), 28); Field("Top P", profile.TopP.ToString(CultureInfo.InvariantCulture), 28, 49);
    Field("Top K", profile.TopK.ToString(), 31); Field("Min P", profile.MinP.ToString(CultureInfo.InvariantCulture), 31, 49);
    Field("Repeat penalty", profile.RepeatPenalty.ToString(CultureInfo.InvariantCulture), 34); Field("Repeat last N", profile.RepeatLastN.ToString(), 34, 49);
    Field("Presence penalty", profile.PresencePenalty.ToString(CultureInfo.InvariantCulture), 37); Field("Frequency penalty", profile.FrequencyPenalty.ToString(CultureInfo.InvariantCulture), 37, 49);
    Field("Batch", profile.Batch.ToString(), 40); Field("Micro batch", profile.UBatch.ToString(), 40, 49);
    Field("Chat template", profile.ChatTemplate, 43); Field("Reasoning / budget", $"{profile.Reasoning} {profile.ReasoningBudget}", 43, 49);
    Field("Extra args (quoted when needed)", ArgumentText.Format(profile.ExtraArgs), 46, 2, 90);
    var vision = new CheckBox { X = 2, Y = 49, Text = "Use vision", Value = profile.Vision ? CheckState.Checked : CheckState.UnChecked };
    var jinja = new CheckBox { X = 20, Y = 49, Text = "Jinja", Value = profile.Jinja ? CheckState.Checked : CheckState.UnChecked };
    var metrics = new CheckBox { X = 36, Y = 49, Text = "Metrics", Value = profile.Metrics ? CheckState.Checked : CheckState.UnChecked };
    var mmap = new CheckBox { X = 52, Y = 49, Text = "Disable mmap", Value = profile.NoMmap ? CheckState.Checked : CheckState.UnChecked };
    dialog.Add(vision, jinja, metrics, mmap);
    var message = new Label { X = 2, Y = 50, Width = 65, Text = "Vision: Qwen3.6-35B-A3B + matching mmproj-BF16.gguf." };
    var save = new Button { X = 68, Y = 50, Text = "Save", IsDefault = true };
    var cancel = new Button { X = Pos.Right(save) + 1, Y = 50, Text = "Cancel" };
    dialog.Add(message, save, cancel);
    var accepted = false;
    findMmproj.Accepting += (_, _) =>
    {
        var match = VisionProjectorResolver.FindBeside(AppConfig.Expand(T("Model path")));
        if (match.Path is not null) mmproj.Text = match.Path;
        message.Text = match.Message;
    };
    save.Accepting += (_, _) =>
    {
        try
        {
            profile.Name = name.Text.Trim(); profile.Description = T("Description").Trim();
            profile.Model = AppConfig.Expand(T("Model path"));
            profile.Vision = vision.Value == CheckState.Checked;
            profile.Mmproj = AppConfig.Expand(T("Vision projector (mmproj)"));
            if (profile.Vision && string.IsNullOrWhiteSpace(profile.Mmproj))
            {
                var match = VisionProjectorResolver.FindBeside(profile.Model);
                if (match.Path is null) throw new InvalidOperationException(match.Message);
                profile.Mmproj = match.Path;
            }
            profile.LlamaServer = AppConfig.Expand(T("llama-server override (blank = global)"));
            profile.Host = T("Host").Trim(); profile.Port = ParseInt(T("Port"), "Port");
            profile.Ctx = ParseInt(T("Context"), "Context"); profile.Ngl = ParseInt(T("GPU layers"), "GPU layers");
            profile.Parallel = ParseInt(T("Parallel"), "Parallel"); profile.Threads = ParseInt(T("Threads (0 = auto)"), "Threads");
            profile.FlashAttn = T("Flash attention (auto/on/off)").Trim().ToLowerInvariant(); profile.Alias = T("Alias").Trim();
            profile.CacheK = T("Cache K (q4_0/q8_0/f16/blank)").Trim(); profile.CacheV = T("Cache V (q4_0/q8_0/f16/blank)").Trim();
            profile.Temp = ParseDouble(T("Temperature"), "Temperature"); profile.TopP = ParseDouble(T("Top P"), "Top P");
            profile.TopK = ParseInt(T("Top K"), "Top K"); profile.MinP = ParseDouble(T("Min P"), "Min P");
            profile.RepeatPenalty = ParseDouble(T("Repeat penalty"), "Repeat penalty"); profile.RepeatLastN = ParseInt(T("Repeat last N"), "Repeat last N");
            profile.PresencePenalty = ParseDouble(T("Presence penalty"), "Presence penalty"); profile.FrequencyPenalty = ParseDouble(T("Frequency penalty"), "Frequency penalty");
            profile.Batch = ParseInt(T("Batch"), "Batch"); profile.UBatch = ParseInt(T("Micro batch"), "Micro batch");
            profile.ChatTemplate = T("Chat template").Trim();
            var reasoning = T("Reasoning / budget").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            profile.Reasoning = reasoning.FirstOrDefault() ?? "auto"; profile.ReasoningBudget = reasoning.Length > 1 ? ParseInt(reasoning[1], "Reasoning budget") : -1;
            profile.ExtraArgs = ArgumentText.Parse(T("Extra args (quoted when needed)"));
            profile.Jinja = jinja.Value == CheckState.Checked; profile.Metrics = metrics.Value == CheckState.Checked; profile.NoMmap = mmap.Value == CheckState.Checked;
            profile.Validate(); accepted = true; app.RequestStop();
        }
        catch (Exception ex) { message.Text = ex.Message; }
    };
    cancel.Accepting += (_, _) => app.RequestStop();
    app.Run(dialog);
    return accepted;
}

static int ParseInt(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new FormatException($"{name} must be a whole number.");
static double ParseDouble(string value, string name) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new FormatException($"{name} must be a number.");
static string CompactModelSize(string path)
{
    var size = ModelSize(path);
    return size.Replace(" KiB", "K", StringComparison.Ordinal)
               .Replace(" MiB", "M", StringComparison.Ordinal)
               .Replace(" GiB", "G", StringComparison.Ordinal)
               .Replace(" TiB", "T", StringComparison.Ordinal)
               .Replace(" B", "B", StringComparison.Ordinal);
}
static string ModelSize(string path)
{
    try
    {
        var bytes = new FileInfo(path).Length;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }
    catch { return ""; }
}

static void ShowHistory(IApplication app, AppConfig cfg, Profile profile)
{
    List<RunRecordRef> entries;
    try { entries = RunHistory.ForProfile(cfg.RunsDir, profile.Name); }
    catch (Exception ex) { MessageBox.ErrorQuery(app, "Run history", ex.Message, "OK"); return; }
    var window = new Window { Title = $" Run history · {profile.Name} ", Width = Dim.Percent(90), Height = Dim.Percent(90) };
    window.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc || key.AsGrapheme.Equals("q", StringComparison.OrdinalIgnoreCase)) { app.RequestStop(); key.Handled = true; } };
    var items = new ObservableCollection<string>(entries.Select(x => $"{x.Record.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}  exit {x.Record.ExitCode}  {x.Record.DurationSeconds:F1}s  gen {x.Record.EvalTokensPerSecond:F2} tok/s"));
    if (items.Count == 0) items.Add("No runs recorded for this profile.");
    var runs = new ListView { X = 0, Y = 0, Width = Dim.Percent(45), Height = Dim.Fill(3) };
    runs.SetSource(items);
#pragma warning disable CS0618
    var detail = new TextView { X = Pos.Right(runs) + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true };
#pragma warning restore CS0618
    void Refresh()
    {
        if (entries.Count == 0) { detail.Text = "No run details."; return; }
        var r = entries[Math.Clamp(runs.SelectedItem ?? 0, 0, entries.Count - 1)].Record;
        detail.Text = $"Run {r.RunId}\nStarted {r.StartedAt.LocalDateTime}\nDuration {r.DurationSeconds:F2}s  Exit {r.ExitCode}\nCommand {r.GeneratedCommand}\nPrompt {r.PromptTokensPerSecond:F2} tok/s  Eval {r.EvalTokensPerSecond:F2} tok/s\nOffload {r.OffloadedLayers}/{r.TotalLayers}\n\nNotes\n{r.Notes}";
    }
    runs.ValueChanged += (_, _) => Refresh();
    var annotate = new Button { X = 1, Y = Pos.Bottom(runs), Text = "Edit note" };
    var close = new Button { X = Pos.Right(annotate) + 2, Y = Pos.Bottom(runs), Text = "Close" };
    annotate.Accepting += (_, _) =>
    {
        if (entries.Count == 0) return;
        var entry = entries[Math.Clamp(runs.SelectedItem ?? 0, 0, entries.Count - 1)];
        var editor = new Window { Title = " Run note ", Width = Dim.Percent(80), Height = Dim.Percent(80) };
        editor.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { app.RequestStop(); key.Handled = true; } };
#pragma warning disable CS0618
        var text = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), Text = entry.Record.Notes };
#pragma warning restore CS0618
        var save = new Button { X = 1, Y = Pos.Bottom(text), Text = "Save", IsDefault = true };
        save.Accepting += (_, _) => { entry.Record.Notes = text.Text.Trim(); RunHistory.Update(entry.Path, entry.Record); app.RequestStop(); };
        editor.Add(text, save); app.Run(editor); Refresh();
    };
    close.Accepting += (_, _) => app.RequestStop();
    window.Add(runs, detail, annotate, close); Refresh(); app.Run(window);
}

static void ShowStartupFailureAnalysis(IApplication app, AppConfig cfg, Profile profile, RunRecord run)
{
    var window = new Window { Title = " Startup failure analysis ", Width = Dim.Percent(90), Height = Dim.Percent(85) };
#pragma warning disable CS0618
    var report = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true, Text = StartupFailureAnalysis.Create(profile, run, cfg.LogsDir) };
#pragma warning restore CS0618
    LltopTheme.ApplyAnalysis(report);
    var close = new Button { X = 1, Y = Pos.Bottom(report), Text = "Back", IsDefault = true };
    close.Accepting += (_, _) => app.RequestStop();
    window.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { app.RequestStop(); key.Handled = true; } };
    window.Add(report, close);
    app.Run(window);
}

static FirstLaunchAction ShowFirstLaunchAdvisor(IApplication app, AppConfig cfg, Profile profile, ServerCapabilityRecord capability, ServerCapabilityRecord suggestedCapability)
{
    var architecture = "unknown";
    try { architecture = GgufMetadataReader.Read(profile.Model).String("general.architecture") ?? architecture; }
    catch { }
    var isDiffusion = architecture.Contains("diffusion", StringComparison.OrdinalIgnoreCase) || profile.Model.Contains("diffusion", StringComparison.OrdinalIgnoreCase);
    var selectedRuntimePath = string.IsNullOrWhiteSpace(capability.BinaryPath) ? profile.LlamaServer : capability.BinaryPath;
    var suggestedRuntimePath = string.IsNullOrWhiteSpace(suggestedCapability.BinaryPath) ? cfg.LlamaServer : suggestedCapability.BinaryPath;
    var suggestedRuntime = Path.GetFileName(suggestedRuntimePath);
    var suggestedBackend = string.IsNullOrWhiteSpace(suggestedCapability.Backend) ? "unknown" : suggestedCapability.Backend;
    var usesSuggestedRuntime = string.Equals(Path.GetFullPath(selectedRuntimePath), Path.GetFullPath(suggestedRuntimePath), StringComparison.OrdinalIgnoreCase);
    var advice = isDiffusion
        ? "⚠ This is an experimental diffusion-style GGUF. Select a diffusion-enabled llama-server runtime before launching, unless you intend to test this runtime."
        : "No known compatibility issue was detected. This configuration has not been run yet.";
    var report = $"New configuration — no previous run.\n\n" +
                 $"Model        {Path.GetFileName(profile.Model)}\n" +
                 $"Architecture {architecture}\n\n" +
                 $"Suggested backend\n" +
                 $"Runtime      {suggestedRuntime}\n" +
                 $"Version      llama.cpp {suggestedCapability.BuildSummary}\n" +
                 $"Backend      {suggestedBackend}\n" +
                 $"Path         {suggestedRuntimePath}\n" +
                 $"GPU layers   {profile.Ngl}\n\n" +
                 $"{(capability.BinaryExists ? "✅" : "❌")} Runtime file {(capability.BinaryExists ? "found" : "missing")}\n" +
                 $"{(capability.VersionProbeSucceeded ? "✅" : "⚠")} --version {(capability.VersionProbeSucceeded ? "succeeds" : "did not succeed")}\n" +
                 $"{(capability.HelpProbeSucceeded ? "✅" : "⚠")} --help {(capability.HelpProbeSucceeded ? "succeeds" : "did not succeed")}" +
                 (usesSuggestedRuntime ? "" : $"\n⚠ Profile uses {Path.GetFileName(selectedRuntimePath)} instead of the suggested runtime.") +
                 "\n\n" +
                 advice;
    var window = new Window { Title = " First launch advisor ", Width = Dim.Percent(80), Height = 26 };
    window.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { app.RequestStop(); key.Handled = true; } };
    window.Add(new Label { X = 1, Y = 1, Width = Dim.Fill(2), Height = 18, Text = report });
    var action = FirstLaunchAction.Cancel;
    var cancel = new Button { X = 1, Y = 21, Text = "Cancel" };
    var edit = new Button { X = Pos.Right(cancel) + 2, Y = 21, Text = "Edit profile" };
    var preview = new Button { X = Pos.Right(edit) + 2, Y = 21, Text = "Preview command" };
    var launch = new Button { X = Pos.Right(preview) + 2, Y = 21, Text = "Launch", IsDefault = true };
    cancel.Accepting += (_, _) => app.RequestStop();
    edit.Accepting += (_, _) => { action = FirstLaunchAction.Edit; app.RequestStop(); };
    preview.Accepting += (_, _) => { action = FirstLaunchAction.Preview; app.RequestStop(); };
    launch.Accepting += (_, _) => { action = FirstLaunchAction.Launch; app.RequestStop(); };
    window.Add(cancel, edit, preview, launch);
    app.Run(window);
    return action;
}

static bool RunFirstRunWizard(IApplication app, AppConfig cfg)
{
    var wizard = new Window { Title = " Welcome to lltop ", Width = 90, Height = 19 };
    wizard.Add(new Label { X = 2, Y = 1, Text = "Connect lltop to your llama.cpp installation." });
    wizard.Add(new Label { X = 2, Y = 3, Text = "llama-server binary or app directory" });
    var server = new TextField { X = 2, Y = 4, Width = Dim.Fill(4), Text = "~/llama/app" };
    wizard.Add(server, new Label { X = 2, Y = 6, Text = "Models directory" });
    var models = new TextField { X = 2, Y = 7, Width = Dim.Fill(4), Text = "~/llama/models" };
    var message = new Label { X = 2, Y = 10, Width = Dim.Fill(4), Height = 2, Text = "Both paths must already exist. Esc cancels setup." };
    var save = new Button { X = 2, Y = 14, Text = "Save and continue", IsDefault = true };
    var cancel = new Button { X = Pos.Right(save) + 2, Y = 14, Text = "Cancel" };
    wizard.Add(models, message, save, cancel);
    var completed = false;
    save.Accepting += (_, _) =>
    {
        try
        {
            var input = AppConfig.Expand(server.Text);
            var serverPath = File.Exists(input) ? input : Path.Combine(input, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
            var modelsPath = AppConfig.Expand(models.Text);
            if (!File.Exists(serverPath)) throw new InvalidOperationException("llama-server was not found at that location.");
            if (!Directory.Exists(modelsPath)) throw new InvalidOperationException("Models directory was not found.");
            cfg.LlamaServer = serverPath;
            cfg.ModelsDir = modelsPath;
            cfg.Save();
            FirstRunProfiles.ScanAndGenerate(cfg, new ServerCapabilityCache(Path.Combine(Path.GetDirectoryName(AppConfig.ConfigPath) ?? cfg.LogsDir, "server-capabilities.json")).Get(cfg.LlamaServer));
            completed = true;
            app.RequestStop();
        }
        catch (Exception ex) { message.Text = ex.Message; }
    };
    cancel.Accepting += (_, _) => app.RequestStop();
    app.Run(wizard);
    return completed;
}

ServerCapabilityRecord CapabilitiesFor(Profile profile)
{
    var executable = string.IsNullOrWhiteSpace(profile.LlamaServer) ? cfg.LlamaServer : profile.LlamaServer;
    return capabilityCache.Get(executable);
}

LaunchPlan LaunchPlanFor(Profile profile, ServerCapabilityRecord capabilities)
{
    var executable = string.IsNullOrWhiteSpace(profile.LlamaServer) ? cfg.LlamaServer : profile.LlamaServer;
    return ServerRunner.BuildLaunchPlan(executable, profile, capabilities);
}

static string FormatPlanCommand(LaunchPlan plan) => string.Join(' ', new[] { plan.Executable }.Concat(plan.FilteredArguments));

static string FormatPlanSummary(LaunchPlan plan)
{
    var command = FormatPlanCommand(plan);
    return plan.RemovedArguments.Count == 0
        ? command
        : $"{command}\nRemoved unsupported options: {string.Join(", ", plan.RemovedArguments.Select(x => x.Display).Distinct(StringComparer.Ordinal))}";
}

enum FirstLaunchAction { Cancel, Edit, Preview, Launch }
