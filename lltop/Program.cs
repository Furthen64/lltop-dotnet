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
var logFrame = new FrameView { Title = " Live log ", X = Pos.Right(profileFrame), Y = 2, Width = Dim.Fill(), Height = Dim.Fill(13) };
var profileList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
var logView = new LogTextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = false, Text = "Waiting for a server launch…" };
profileFrame.Add(profileList); logFrame.Add(logView);
var statusFrame = new FrameView { Title = " Selected profile / server ", X = 0, Y = Pos.Bottom(profileFrame), Width = Dim.Fill(), Height = 10 };
var status = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(), Text = "Loading…" };
statusFrame.Add(status);
var help = new Label { X = 1, Y = Pos.Bottom(statusFrame), Width = Dim.Fill(2), Height = 3,
    Text = "[Enter] Start   [s] Stop   [r] Restart   [n] New profile   [F5] Find models\n[↑/↓] Select   [v] Preview   [N] History   [h/?] All keys   [q] Quit" };
var resourceStrip = new ResourceStripView { X = 1, Y = Pos.Bottom(help), Width = Dim.Fill(2) };
win.Add(banner, profileFrame, logFrame, statusFrame, help, resourceStrip);
LltopTheme.Apply([profileFrame, logFrame, statusFrame], banner, profileList, logView, help);

ISystemResourceProvider resourceProvider = OperatingSystem.IsLinux()
    ? new LinuxSystemResourceProvider(
        () => (resourceGpuBackend, resourceGpuName),
        () => runner.State == RunnerState.Running || externalServer is not null ? 1 : 0)
    : new UnavailableSystemResourceProvider(() => runner.State == RunnerState.Running || externalServer is not null ? 1 : 0);

void ApplyLayout()
{
    var helpHeight = expandedHelp ? 5 : 2;
    help.Height = helpHeight;
    help.Text = expandedHelp
        ? "NAVIGATION  [↑/↓] Select   [Enter] Start   [q/Esc] Quit\nSERVER      [s] Stop   [K] Force stop   [r] Restart   [v] Preview   [c] Copy command\nPROFILES    [n] New   [e] Edit   [d] Duplicate   [x] Delete   [F5] Find models\nLOG & RUNS  [l] Auto-scroll   [PgUp/PgDn] Scroll   [Home/End] Jump   [N] History\nHELP        [h/?] Show fewer keys"
        : "[Enter] Start   [s] Stop   [r] Restart   [n] New profile   [F5] Find models\n[↑/↓] Select   [v] Preview   [N] History   [h/?] All keys   [q] Quit";
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
win.ViewportChanged += (_, _) => ApplyLayout();
ApplyLayout();

void RefreshProfileItems(string? selectName = null)
{
    profileItems.Clear();
    if (profiles.Count == 0) profileItems.Add("  No profiles yet — press n to create one");
    else foreach (var p in profiles)
    {
        var marker = p.Name.Equals(runningProfile, StringComparison.OrdinalIgnoreCase)
            ? runner.State == RunnerState.Running ? "●" : "◐" : HasRun(cfg.RunsDir, p.Name) ? "●" : "○";
        var size = CompactModelSize(p.Model);
        var text = $"{marker} {p.Name}{(size.Length == 0 ? "" : $"  {size}")}";
        var width = Math.Max(12, profileFrame.Viewport.Width > 0 ? profileFrame.Viewport.Width - 3 : 32);
        profileItems.Add(FitInline(text, width));
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
    var model = string.IsNullOrWhiteSpace(p.Model) ? "not configured" : p.Model;
    var description = string.IsNullOrWhiteSpace(p.Description) ? "—" : p.Description;
    var gpu = GpuLaunchInfo.ForProfile(p);
    var capability = CapabilitiesFor(p);
    resourceGpuBackend = capability.Backend;
    resourceGpuName = capability.GpuName;
    var plan = LaunchPlanFor(p, capability);
    ProfileRunSummary? summary = null;
    try
    {
        if (!historySummaries.TryGetValue(p.Name, out summary)) historySummaries[p.Name] = summary = RunHistory.Summarize(cfg.RunsDir, p.Name);
    }
    catch { }
    var history = summary is null ? "" : $"\nHISTORY  {summary.RunCount} runs  gen latest {summary.Generation.Latest:F2} avg {summary.Generation.Average:F2} tok/s  {RunHistory.Sparkline(summary.Generation.Series)}";
    var issue = serverStats.LastError.Length > 0 ? $"\nERROR    {serverStats.LastError}" : serverStats.LastHint.Length > 0 ? $"\nHINT     {serverStats.LastHint}" : "";
    var backend = string.IsNullOrWhiteSpace(capability.Backend) ? "unknown" : capability.Backend;
    var gpuName = string.IsNullOrWhiteSpace(capability.GpuName) ? "unknown" : capability.GpuName;
    var compute = string.IsNullOrWhiteSpace(capability.ComputeCapability) ? "unknown" : capability.ComputeCapability;
    var removed = plan.RemovedArguments.Count == 0 ? "" : $"\nFILTER   removed {string.Join(", ", plan.RemovedArguments.Select(x => x.OptionName).Distinct(StringComparer.Ordinal))}";
    var probe = string.IsNullOrWhiteSpace(capability.ProbeMessage) ? "" : $"\nPROBE    {capability.ProbeMessage}";
    status.Text = $"STATE    {state}{pid}{uptime}     PROFILE  {p.Name}     BIND  {p.Host}:{p.Port}\n" +
                  $"MODEL    {model}\n" +
                  $"DETAIL   ctx {p.Ctx:N0}  gpu layers {p.Ngl}  parallel {p.Parallel}  flash-attn {p.FlashAttn}\n" +
                  $"GPU      {gpu.Summary}\n" +
                  $"SERVER   backend {backend}  gpu {gpuName}  cc {compute}\n" +
                  $"BUILD    llama.cpp {capability.BuildSummary}  compatibility {capability.CompatibilityMode}\n" +
                  $"METRIC   prompt {serverStats.PromptTokensPerSecond:F2} tok/s  eval {serverStats.EvalTokensPerSecond:F2} tok/s  offload {serverStats.OffloadedLayers}/{serverStats.TotalLayers}  progress {serverStats.Progress:P0}\n" +
                  $"ABOUT    {description}" + history + removed + probe + issue + (string.IsNullOrWhiteSpace(message) ? "" : $"\nINFO     {message}");
}

void RefreshLogs()
{
    logView.Text = logLines.Count == 0 ? "Waiting for server output…" : string.Join('\n', logLines);
    if (logAutoScroll) logView.MoveEnd();
    else logView.ScrollTo(new System.Drawing.Point(0, Math.Clamp(logScrollRow, 0, Math.Max(0, logLines.Count - 1))));
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
    RunHistory.Save(cfg.RunsDir, RunRecord.Create(profile, runner.Command, started, DateTimeOffset.Now, exit.ExitCode, exit.Requested ? "stopped" : "exit", stats));
    historySummaries.Remove(profile.Name);
}

void ReloadProfiles(string? selectName = null, string message = "Profiles reloaded.")
{
    var result = store.LoadAll();
    profiles = result.Profiles;
    historySummaries.Clear();
    RefreshProfileItems(selectName);
    var suffix = result.Errors.Count == 0 ? message : $"{message}  Skipped: {string.Join(" | ", result.Errors)}";
    UpdateStatus(suffix);
}

void RefreshModels()
{
    try
    {
        var result = FirstRunProfiles.ScanAndGenerate(cfg, capabilityCache.Get(cfg.LlamaServer));
        ReloadProfiles(message: $"Refresh complete: found {result.ModelsFound} models, created {result.ProfilesCreated} profiles.");
    }
    catch (Exception ex) { UpdateStatus($"Refresh failed: {ex.Message}"); }
}

async Task Launch(bool restart = false)
{
    var profile = SelectedProfile();
    if (profile is null) { UpdateStatus("Create a profile first (n)."); return; }
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
        if (!restart && cfg.ConfirmRecentFailure)
        {
            var recent = RunHistory.FindRecentFailure(cfg.RunsDir, profile, cfg.RecentFailureWindowSeconds, cfg.StartupFailureSeconds);
            if (recent is not null && MessageBox.Query(app, "Recent startup failure", $"This configuration failed recently (exit {recent.ExitCode}, {recent.DurationSeconds:F1}s).\n\nRun it again?", "Cancel", "Run again") != 1) return;
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
runner.StateChanged += _ => app.Invoke(() => { RefreshProfileItems(runningProfile); UpdateStatus(); });
runner.Exited += exit => app.Invoke(() =>
{
    var name = runningProfile; runningProfile = ""; RefreshProfileItems(name);
    try { SaveActiveRun(exit); }
    catch (Exception ex) { UpdateStatus($"Could not save run history: {ex.Message}"); }
    UpdateStatus(exit.Requested ? $"Server stopped (exit {exit.ExitCode})." : $"Server exited with code {exit.ExitCode}." + (exit.Error is null ? "" : $" {exit.Error}"));
});
profileList.ValueChanged += (_, _) =>
{
    if (profileList.SelectedItem is int value && profiles.Count > 0) selected = Math.Clamp(value, 0, profiles.Count - 1);
    UpdateStatus();
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
    else if (text.Equals("r", StringComparison.OrdinalIgnoreCase)) { _ = Launch(true); key.Handled = true; }
    else if (text == "n") { NewProfile(); key.Handled = true; }
    else if (text.Equals("e", StringComparison.OrdinalIgnoreCase)) { EditSelected(); key.Handled = true; }
    else if (text.Equals("d", StringComparison.OrdinalIgnoreCase)) { DuplicateSelected(); key.Handled = true; }
    else if (text.Equals("x", StringComparison.OrdinalIgnoreCase)) { DeleteSelected(); key.Handled = true; }
    else if (text.Equals("v", StringComparison.OrdinalIgnoreCase))
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
    else if (text == "N")
    {
        var p = SelectedProfile();
        if (p is null) UpdateStatus("No profile selected."); else ShowHistory(app, cfg, p);
        profileList.SetFocus(); UpdateStatus(); key.Handled = true;
    }
    else if (text is "h" or "H" or "?") { expandedHelp = !expandedHelp; ApplyLayout(); key.Handled = true; }
    else if (key.KeyCode == KeyCode.F5) { RefreshModels(); key.Handled = true; }
    else if (text.Equals("q", StringComparison.OrdinalIgnoreCase) || key.KeyCode == KeyCode.Esc) { _ = Quit(); key.Handled = true; }
};

RefreshProfileItems();
UpdateStatus(load.Errors.Count == 0 ? cfg.LoadMessage : $"Skipped invalid profiles: {string.Join(" | ", load.Errors)}");
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
app.Run(win);
monitorCancellation.Cancel();
runner.Dispose();

static bool EditProfile(IApplication app, Profile profile, string title)
{
    var dialog = new Window { Title = $" {title} ", Width = 96, Height = 45 };
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
    Field("llama-server override (blank = global)", profile.LlamaServer, 7, 2, 90);
    Field("Host", profile.Host, 10); Field("Port", profile.Port.ToString(), 10, 49);
    Field("Context", profile.Ctx.ToString(), 13); Field("GPU layers", profile.Ngl.ToString(), 13, 49);
    Field("Parallel", profile.Parallel.ToString(), 16); Field("Threads (0 = auto)", profile.Threads.ToString(), 16, 49);
    Field("Flash attention (auto/on/off)", profile.FlashAttn, 19); Field("Alias", profile.Alias, 19, 49);
    Field("Cache K", profile.CacheK, 22); Field("Cache V", profile.CacheV, 22, 49);
    Field("Temperature", profile.Temp.ToString(CultureInfo.InvariantCulture), 25); Field("Top P", profile.TopP.ToString(CultureInfo.InvariantCulture), 25, 49);
    Field("Top K", profile.TopK.ToString(), 28); Field("Min P", profile.MinP.ToString(CultureInfo.InvariantCulture), 28, 49);
    Field("Batch", profile.Batch.ToString(), 31); Field("Micro batch", profile.UBatch.ToString(), 31, 49);
    Field("Chat template", profile.ChatTemplate, 34); Field("Reasoning / budget", $"{profile.Reasoning} {profile.ReasoningBudget}", 34, 49);
    Field("Extra args (quoted when needed)", ArgumentText.Format(profile.ExtraArgs), 37, 2, 90);
    var jinja = new CheckBox { X = 2, Y = 40, Text = "Jinja", Value = profile.Jinja ? CheckState.Checked : CheckState.UnChecked };
    var metrics = new CheckBox { X = 20, Y = 40, Text = "Metrics", Value = profile.Metrics ? CheckState.Checked : CheckState.UnChecked };
    var mmap = new CheckBox { X = 40, Y = 40, Text = "Disable mmap", Value = profile.NoMmap ? CheckState.Checked : CheckState.UnChecked };
    dialog.Add(jinja, metrics, mmap);
    var message = new Label { X = 58, Y = 40, Width = Dim.Fill(2), Text = "Tab moves between fields." };
    var save = new Button { X = 2, Y = 41, Text = "Save", IsDefault = true };
    var cancel = new Button { X = Pos.Right(save) + 2, Y = 41, Text = "Cancel" };
    dialog.Add(message, save, cancel);
    var accepted = false;
    save.Accepting += (_, _) =>
    {
        try
        {
            profile.Name = name.Text.Trim(); profile.Description = T("Description").Trim();
            profile.Model = AppConfig.Expand(T("Model path"));
            profile.LlamaServer = AppConfig.Expand(T("llama-server override (blank = global)"));
            profile.Host = T("Host").Trim(); profile.Port = ParseInt(T("Port"), "Port");
            profile.Ctx = ParseInt(T("Context"), "Context"); profile.Ngl = ParseInt(T("GPU layers"), "GPU layers");
            profile.Parallel = ParseInt(T("Parallel"), "Parallel"); profile.Threads = ParseInt(T("Threads (0 = auto)"), "Threads");
            profile.FlashAttn = T("Flash attention (auto/on/off)").Trim().ToLowerInvariant(); profile.Alias = T("Alias").Trim();
            profile.CacheK = T("Cache K").Trim(); profile.CacheV = T("Cache V").Trim();
            profile.Temp = ParseDouble(T("Temperature"), "Temperature"); profile.TopP = ParseDouble(T("Top P"), "Top P");
            profile.TopK = ParseInt(T("Top K"), "Top K"); profile.MinP = ParseDouble(T("Min P"), "Min P");
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
static bool HasRun(string directory, string profile) { try { return RunHistory.ForProfile(directory, profile).Count > 0; } catch { return false; } }
static string FitInline(string value, int width)
{
    if (width <= 0 || value.Length <= width) return value;
    if (width <= 3) return value[..width];
    return value[..(width - 3)] + "...";
}
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
