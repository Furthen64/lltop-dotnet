using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

SingleInstanceLock? instanceLock;
try { instanceLock = SingleInstanceLock.TryAcquire(); }
catch (Exception ex)
{
    Console.Error.WriteLine($"lltop could not acquire its instance lock: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
using var instanceLease = instanceLock;
if (instanceLease is null)
{
    Console.Error.WriteLine("lltop is already running on this host. Close the existing instance before starting another.");
    Environment.ExitCode = 1;
    return;
}

using var app = Application.Create().Init();
var cfg = AppConfig.Load();
var knownTheme = LltopTheme.Select(cfg.Theme);
if (cfg.IsFirstRun && !RunFirstRunWizard(app, cfg)) return;

var store = new ProfileStore(cfg.ProfilesDir);
var removedLegacyStarter = FirstRunProfiles.RemoveLegacyStarter(cfg);
var load = store.LoadAll();
var profiles = load.Profiles;
var selected = Math.Max(0, profiles.FindIndex(p => p.Name.Equals(cfg.DefaultProfile, StringComparison.OrdinalIgnoreCase)));
var selectedProfileListItem = 0;
var runner = new ServerRunner();
var capabilityCache = new ServerCapabilityCache(Path.Combine(Path.GetDirectoryName(AppConfig.ConfigPath) ?? cfg.LogsDir, "server-capabilities.json"));
var runningProfile = "";
Profile? activeProfile = null;
var serverStats = new ServerStats();
var activeRunGate = new object();
var logLines = new List<string>();
var activeRunSamples = new List<RunResourceSample>();
RunGraphDataWriter? activeRunGraphData = null;
var profileItems = new ObservableCollection<string>();
var historySummaries = new Dictionary<string, ProfileRunSummary>(StringComparer.OrdinalIgnoreCase);
var closing = false;
var logAutoScroll = true;
var logScrollRow = 0;
var showingResourceGraph = false;
DateTimeOffset? logAutoScrollPausedAt = null;
var expandedHelp = false;
var externalMonitor = new ExternalServerMonitor(cfg);
ExternalServer? externalServer = null;
var benchmarkActive = false;
CancellationTokenSource? benchmarkCancellation = null;
BenchmarkRecord? activeBenchmark = null;
var resourceGpuBackend = "";
var resourceGpuName = "";
using var monitorCancellation = new CancellationTokenSource();
_ = capabilityCache.Get(cfg.LlamaServer);

var win = new Window { Title = " lltop · llama.cpp control center ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
var banner = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Text = "LLAMA SERVER  •  profiles, launches, and live output" };
var profileFrame = new FrameView { Title = " Profiles ", X = 0, Y = 2, Width = Dim.Percent(34), Height = Dim.Fill(13) };
var logFrame = new FrameView { Title = " Profile overview ", X = Pos.Right(profileFrame), Y = 2, Width = Dim.Fill(), Height = Dim.Fill(13) };
var profileList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
var logView = new LogTextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), ReadOnly = true, WordWrap = false, Text = "Waiting for a server launch…" };
var logStatus = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
profileFrame.Add(profileList); logFrame.Add(logView, logStatus);
var statusFrame = new FrameView { Title = " Selected profile / server ", X = 0, Y = Pos.Bottom(profileFrame), Width = Dim.Percent(34), Height = 10 };
var status = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(), Text = "Loading…" };
statusFrame.Add(status);
var metricsFrame = new FrameView { Title = " Metrics ", X = Pos.Right(statusFrame), Y = Pos.Bottom(logFrame), Width = Dim.Fill(), Height = 10 };
var metrics = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(), Text = "Waiting for the first request…" };
metricsFrame.Add(metrics);
var help = new Label { X = 1, Y = Pos.Bottom(statusFrame), Width = Dim.Fill(2), Height = 3,
    Text = "[Enter] Start   [e/F2] Edit   [d] Duplicate   [x] Delete   [n] New   [Ctrl+F] Favorite\n[↑/↓] Select   [s] Stop   [g] Graph   [H] History   [h/?] All keys   [q] Quit" };
var resourceStrip = new ResourceStripView { X = 1, Y = Pos.Bottom(help), Width = Dim.Fill(2) };
win.Add(banner, profileFrame, logFrame, statusFrame, metricsFrame, help, resourceStrip);
LltopTheme.Apply([profileFrame, logFrame, statusFrame, metricsFrame], banner, profileList, logView, status, metrics, help, logStatus);

ISystemResourceProvider resourceProvider = OperatingSystem.IsLinux()
    ? new LinuxSystemResourceProvider(
        () => (resourceGpuBackend, resourceGpuName),
        () => runner.State == RunnerState.Running || externalServer is not null || benchmarkActive ? 1 : 0,
        serverProcessId: () => runner.ProcessId)
    : new UnavailableSystemResourceProvider(() => runner.State == RunnerState.Running || externalServer is not null || benchmarkActive ? 1 : 0);
var benchmarkRunner = new BenchmarkRunner(
    cfg,
    profile => LaunchPlanFor(profile, CapabilitiesFor(profile)),
    () => runner.IsActive || externalServer is not null,
    resourceProvider);

void ApplyLayout()
{
    var helpHeight = expandedHelp ? 6 : 2;
    help.Height = helpHeight;
    help.Text = expandedHelp
        ? "NAVIGATION  [↑/↓] Select   [Enter] Start   [q/Esc] Quit\nSERVER      [s] Stop   [K] Force stop   [r] Restart   [p] Preview   [c] Copy command\nPROFILES    [n] New   [e/F2] Edit   [d] Duplicate   [x] Delete   [Ctrl+F] Favorite/unfavorite   [Ctrl+R/F5] Find models\nBENCHMARK   [b] Setup/start   [B] Cancel   idle server required   reports → benchmarks_dir\nLOG & RUNS  [g] Resource graph   [l] Toggle follow   [↑/PgUp] Pause log follow   [↓/PgDn/End] Resume at bottom   [H] History\nTHEME       [t] Cycle theme ({LltopTheme.CurrentName})   [h/?] Show fewer keys"
        : $"[Enter] Start   [e/F2] Edit   [d] Duplicate   [x] Delete   [n] New   [Ctrl+F] Favorite\n[↑/↓] Select   [s] Stop   [g] Graph   [H] History   [t] Theme: {LltopTheme.CurrentName}   [h/?] All keys   [q] Quit";
    var narrow = win.Viewport.Width is > 0 and < 84;
    var reserved = (narrow ? 20 : 10) + helpHeight + 1;
    if (narrow)
    {
        var profileHeight = Math.Max(6, (win.Viewport.Height - reserved - 2) / 3);
        profileFrame.X = 0; profileFrame.Y = 2; profileFrame.Width = Dim.Fill(); profileFrame.Height = profileHeight;
        logFrame.X = 0; logFrame.Y = Pos.Bottom(profileFrame); logFrame.Width = Dim.Fill(); logFrame.Height = Dim.Fill(reserved);
        statusFrame.X = 0; statusFrame.Y = Pos.Bottom(logFrame); statusFrame.Width = Dim.Fill();
        metricsFrame.X = 0; metricsFrame.Y = Pos.Bottom(statusFrame); metricsFrame.Width = Dim.Fill();
    }
    else
    {
        profileFrame.X = 0; profileFrame.Y = 2; profileFrame.Width = Dim.Percent(34); profileFrame.Height = Dim.Fill(reserved);
        logFrame.X = Pos.Right(profileFrame); logFrame.Y = 2; logFrame.Width = Dim.Fill(); logFrame.Height = Dim.Fill(reserved);
        statusFrame.X = 0; statusFrame.Y = Pos.Bottom(profileFrame); statusFrame.Width = Dim.Percent(34);
        metricsFrame.X = Pos.Right(statusFrame); metricsFrame.Y = Pos.Bottom(logFrame); metricsFrame.Width = Dim.Fill();
    }
    help.Y = narrow ? Pos.Bottom(metricsFrame) : Pos.Bottom(statusFrame);
    resourceStrip.Y = Pos.Bottom(help);
}
win.ViewportChanged += (_, _) => { ApplyLayout(); RefreshProfileItems(runningProfile); };
// A window viewport notification occurs before its child views necessarily receive
// their final dimensions. Rebuild once the list itself is laid out so the padded
// profile rows use the visible width rather than the startup fallback width.
profileList.ViewportChanged += (_, _) => RefreshProfileItems(runningProfile);
ApplyLayout();

void RefreshProfileItems(string? selectName = null)
{
    profileItems.Clear();
    if (profiles.Count == 0) profileItems.Add("  No profiles yet — press n to create one");
    else
    {
        var rows = new List<UiText.ProfileRowData>(profiles.Count);
        foreach (var p in profiles)
        {
            var summary = SummaryFor(p.Name);
            // Error state deliberately wins: a profile with a known launch failure must
            // remain visible as broken even if it is selected or a new launch is pending.
            var marker = UiText.ProfileGlyph(
                isBroken: !File.Exists(AppConfig.Expand(p.Model)) || summary?.LastExitCode is not null and not 0,
                isRunning: p.Name.Equals(runningProfile, StringComparison.OrdinalIgnoreCase) && runner.State == RunnerState.Running);
            rows.Add(new UiText.ProfileRowData(marker, p.Vision, p.Name, p.Tags, CompactModelSize(p.Model)));
        }
        var width = Math.Max(12, profileFrame.Viewport.Width > 0 ? profileFrame.Viewport.Width - 3 : 32);
        var favoriteCount = profiles.Count(p => p.Favorite);
        foreach (var (line, index) in UiText.ProfileRows(rows, width).Select((line, index) => (line, index)))
        {
            if (index == favoriteCount && favoriteCount > 0) profileItems.Add(new string('─', width));
            profileItems.Add(line);
        }
    }
    profileList.SetSource(profileItems);
    if (profiles.Count == 0) { selected = 0; profileList.SelectedItem = 0; }
    else
    {
        var match = selectName is null ? -1 : profiles.FindIndex(p => p.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase));
        selected = Math.Clamp(match >= 0 ? match : selected, 0, profiles.Count - 1);
        selectedProfileListItem = UiText.ProfileListItem(selected, profiles.Count, profiles.Count(p => p.Favorite));
        profileList.SelectedItem = selectedProfileListItem;
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
        metrics.Text = "Request stats  Waiting for a server launch…";
        return;
    }
    var model = string.IsNullOrWhiteSpace(p.Model) ? "not configured" : Path.GetFileName(p.Model);
    var modelSize = ModelSize(p.Model);
    var gpu = GpuLaunchInfo.ForProfile(p);
    var capability = CapabilitiesFor(p);
    var runtimeBackend = runner.IsActive ? serverStats.RuntimeBackend : "";
    var runtimeGpuName = runner.IsActive ? serverStats.RuntimeGpuName : "";
    var backend = string.IsNullOrWhiteSpace(runtimeBackend)
        ? (string.IsNullOrWhiteSpace(capability.Backend) ? "unknown" : capability.Backend)
        : runtimeBackend;
    var gpuName = string.IsNullOrWhiteSpace(runtimeGpuName) ? capability.GpuName : runtimeGpuName;
    resourceGpuBackend = backend;
    resourceGpuName = gpuName;
    var plan = LaunchPlanFor(p, capability);
    var summary = SummaryFor(p.Name);
    var device = gpu.IsExplicit ? gpu.Summary : "Automatic";
    var runtimeName = Path.GetFileName(capability.BinaryPath);
    var server = $"{runtimeName}  ·  {backend} backend  ·  llama.cpp {capability.BuildSummary}";
    if (!string.IsNullOrWhiteSpace(gpuName)) server += $"  ·  {gpuName}";
    var vision = p.Vision ? $"On  ·  {Path.GetFileName(p.Mmproj)}" : "Off";
    var lastRun = summary?.LastRunAt is { } last
        ? $"{(summary.LastExitCode == 0 ? "Success" : $"Failed (exit {summary.LastExitCode})")}  ·  {UiText.RelativeTime(last, DateTimeOffset.Now)}" +
          (summary.Generation.Latest > 0 ? $"  ·  output {summary.Generation.Latest:F1} tok/s" : "")
        : "No runs recorded";
    var lines = new List<string>
    {
        $"{state}{pid}{uptime}  ·  {p.Name}  ·  {p.Host}:{p.Port}",
        $"Model     {model}{(modelSize.Length == 0 ? "" : $"  ·  {modelSize}")}",
        $"Launch    ctx {p.Ctx:N0}  ·  GPU layers {p.Ngl}  ·  parallel {p.Parallel}  ·  FA {p.FlashAttn}" +
        (p.Mtp ? $"  ·  MTP {p.MtpDraftTokens}" : ""),
        $"Vision    {vision}",
        $"Device    {device}",
        $"Server    {server}",
        $"Last run  {lastRun}"
    };
    var notice = serverStats.LastError.Length > 0 ? $"Error     {serverStats.LastError}"
        : serverStats.LastHint.Length > 0 ? $"Hint      {serverStats.LastHint}"
        : plan.RemovedArguments.Count > 0 ? $"Warning   Unsupported options removed: {string.Join(", ", plan.RemovedArguments.Select(x => x.OptionName).Distinct(StringComparer.Ordinal))}"
        : !string.IsNullOrWhiteSpace(message) ? $"Info      {message}"
        : "";
    lines.Add(notice);
    status.Text = string.Join('\n', lines);
    metrics.Text = runner.IsActive || externalServer is not null
        ? UiText.RequestMetrics(serverStats)
        : "Request stats  Waiting for a server launch…";
}

void RefreshLogs()
{
    if (showingResourceGraph)
    {
        var profile = SelectedProfile();
        var live = profile is not null && profile.Name.Equals(runningProfile, StringComparison.OrdinalIgnoreCase) && runner.IsActive;
        RunRecord? latest = null;
        if (profile is not null && !live)
        {
            try { latest = RunHistory.ForProfile(cfg.RunsDir, profile.Name).FirstOrDefault()?.Record; }
            catch { }
        }
        List<RunResourceSample> samples;
        string? graphDataPath;
        lock (activeRunGate)
        {
            samples = live ? [.. activeRunSamples] : latest?.ResourceSamples ?? [];
            graphDataPath = live ? activeRunGraphData?.Path : latest?.GraphDataPath;
        }
        logFrame.Title = " Resource graph ";
        logView.Text = profile is null ? "No profile selected." : RunResourceGraph.Format(profile.Name, latest, samples, logView.Viewport.Width, live, graphDataPath);
        logView.MoveHome();
        logStatus.Text = live ? "GRAPH  live resource samples · [g] runtime log" : "GRAPH  latest recorded run · [g] runtime log";
        return;
    }
    var showingBenchmark = benchmarkActive && activeBenchmark is not null;
    var showingLogs = !showingBenchmark && (runner.IsActive || externalServer is not null || logLines.Count > 0);
    logFrame.Title = showingBenchmark ? " Benchmark progress " : showingLogs ? " Live log " : " Profile overview ";
    logView.Text = showingBenchmark
        ? FormatBenchmarkProgress(activeBenchmark!)
        : showingLogs
        ? logLines.Count == 0 ? "Starting server; waiting for output…" : string.Join('\n', logLines)
        : FormatProfileOverview(SelectedProfile());
    if (logAutoScroll) logView.MoveEnd();
    else logView.ScrollTo(new System.Drawing.Point(0, Math.Clamp(logScrollRow, 0, Math.Max(0, logLines.Count - 1))));
    UpdateLogStatus();
}

string FormatBenchmarkProgress(BenchmarkRecord benchmark)
{
    var completed = benchmark.Cases.Count(x => x.Status is not BenchmarkCaseStatus.Pending and not BenchmarkCaseStatus.Running);
    var running = benchmark.Cases.FirstOrDefault(x => x.Status == BenchmarkCaseStatus.Running);
    var elapsed = DateTimeOffset.Now - benchmark.StartedAt;
    var lines = new List<string>
    {
        $"Running benchmark for {benchmark.ProfileName}",
        $"Progress      {completed}/{benchmark.Cases.Count} cases completed",
        $"Current       {running?.Label ?? "finishing"}",
        $"Elapsed       {elapsed:hh\\:mm\\:ss}",
        $"OOM policy    {benchmark.OomPolicy}",
        "",
        "Cases"
    };
    lines.AddRange(benchmark.Cases.Select(item =>
    {
        var marker = item.Status switch
        {
            BenchmarkCaseStatus.Completed => "✓",
            BenchmarkCaseStatus.Running => "●",
            BenchmarkCaseStatus.Failed or BenchmarkCaseStatus.OutOfMemory => "!",
            BenchmarkCaseStatus.Cancelled => "×",
            BenchmarkCaseStatus.Skipped => "–",
            _ => "○"
        };
        var detail = item.Status == BenchmarkCaseStatus.Running && item.StartedAt is { } started
            ? $"  {DateTimeOffset.Now - started:mm\\:ss}"
            : item.TelemetryAvailable ? $"  {BenchmarkReport.ProgressVramDetail(item)}"
            : item.Error.Length > 0 ? $"  {item.Error}" : "";
        return $" {marker} {item.Label,-28} {item.Status}{detail}";
    }));
    lines.Add("\nPress B to cancel. Benchmark processes are not added to run history.");
    return string.Join('\n', lines);
}

void UpdateLogStatus()
{
    var source = runner.IsActive
        ? $"llama-server stdio → {runner.LogPath}"
        : externalServer is not null
            ? string.IsNullOrWhiteSpace(externalServer.LogPath) ? "external llama-server (no readable log file)" : $"external log file → {externalServer.LogPath}"
            : "no live log source";
    var mode = logAutoScroll
        ? "FOLLOWING"
        : $"PAUSED — End/PgDn resumes · auto-resumes in {Math.Max(0, 60 - (int)(DateTimeOffset.Now - logAutoScrollPausedAt!.Value).TotalSeconds)}s";
    logStatus.Text = $"LOG  {source}  ·  {mode}";
}

void PauseLogFollow()
{
    if (!logAutoScroll) return;
    logAutoScroll = false;
    logAutoScrollPausedAt = DateTimeOffset.Now;
    logScrollRow = Math.Max(0, logLines.Count - 1);
    UpdateLogStatus();
}

void ResumeLogFollow()
{
    logAutoScroll = true;
    logAutoScrollPausedAt = null;
    logScrollRow = Math.Max(0, logLines.Count - 1);
    RefreshLogs();
}

void ResumeLogFollowWhenIdle()
{
    if (logAutoScroll || logAutoScrollPausedAt is not { } pausedAt) return;
    if (DateTimeOffset.Now - pausedAt >= TimeSpan.FromMinutes(1)) ResumeLogFollow();
    else UpdateLogStatus();
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
    List<RunResourceSample> samples;
    string graphDataPath;
    lock (activeRunGate)
    {
        samples = [.. activeRunSamples];
        graphDataPath = activeRunGraphData?.Path ?? "";
    }
    var ended = DateTimeOffset.Now;
    try
    {
        RunHistory.Save(cfg.RunsDir, RunRecord.Create(profile, runner.Command, started, ended, exit.ExitCode, exit.Requested ? "stopped" : "exit", stats, runner.LogPath, samples, graphDataPath));
    }
    finally
    {
        lock (activeRunGate)
        {
            activeRunGraphData?.WriteEvent(ended, new LlamaRuntimeEvent("run_ended", new Dictionary<string, object?> { ["exit_code"] = exit.ExitCode, ["requested"] = exit.Requested }, ""));
            activeRunGraphData?.Dispose();
            activeRunGraphData = null;
        }
    }
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
            MessageBox.Query(app, "Model discovery", $"No verified GGUF language models were found in:\n{cfg.ModelsDir}\n\nlltop scans up to {FirstRunProfiles.ModelSearchDepth} folders deep and skips invalid, non-language, projector, and ignored files.", "OK");
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
        while (!restart && !RunHistory.HasRunForProfile(cfg.RunsDir, profile.Name))
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
            if (firstLaunch == FirstLaunchAction.SetupVision)
            {
                if (ShowVisionSetup(app, profile))
                {
                    var status = profile.Vision ? $"Vision enabled with {Path.GetFileName(profile.Mmproj)}." : "Vision disabled; this profile will launch text-only.";
                    try { store.Save(profile); ReloadProfiles(profile.Name, status); }
                    catch (Exception ex) { UpdateStatus($"Could not save vision setup: {ex.Message}"); }
                }
                return;
            }
            break;
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
        lock (activeRunGate) { serverStats = new ServerStats(); activeProfile = profile.Copy(profile.Name); activeRunSamples.Clear(); }
        runningProfile = profile.Name;
        await runner.StartAsync(plan, profile, cfg);
        lock (activeRunGate) activeRunGraphData = RunGraphDataWriter.Create(cfg.RunsDir, profile, runner.StartedAt ?? DateTimeOffset.Now);
        RefreshProfileItems(profile.Name);
        UpdateStatus(plan.RemovedArguments.Count == 0
            ? $"Started successfully. Log: {runner.LogPath}  Graph data: {activeRunGraphData.Path}"
            : $"Started with compatibility filtering. Removed: {string.Join(", ", plan.RemovedArguments.Select(x => x.OptionName).Distinct(StringComparer.Ordinal))}. Graph data: {activeRunGraphData.Path}");
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

async Task RunBenchmark()
{
    if (benchmarkActive) { UpdateStatus("A benchmark is already running. Press B to cancel it."); return; }
    if (runner.IsActive || externalServer is not null) { UpdateStatus("Stop the active llama-server before starting a benchmark."); return; }
    var profile = SelectedProfile();
    if (profile is null) { UpdateStatus("Select a profile before starting a benchmark."); return; }
    var setup = ShowBenchmarkSetup(app, profile);
    if (setup is null) return;
    try
    {
        var benchmark = new BenchmarkRecord
        {
            BenchmarkId = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{ProfileStore.Slugify(profile.Name)}",
            ProfileName = profile.Name,
            BaselineProfile = profile.Copy(profile.Name),
            StartedAt = DateTimeOffset.Now,
            Workload = setup.Workload,
            OomPolicy = setup.OomPolicy,
            Sweeps = setup.Sweeps
        };
        // Generate before starting so invalid settings are caught in the setup flow.
        benchmark.Cases = BenchmarkCases.Generate(benchmark.BaselineProfile, benchmark.Sweeps);
        benchmarkActive = true;
        activeBenchmark = benchmark;
        benchmarkCancellation = new CancellationTokenSource();
        UpdateStatus($"Benchmark started: 0/{benchmark.Cases.Count} cases. Press B to cancel.");
        RefreshLogs();
        await benchmarkRunner.RunAsync(benchmark, update => app.Invoke(() =>
        {
            var completed = update.Cases.Count(x => x.Status is not BenchmarkCaseStatus.Pending and not BenchmarkCaseStatus.Running);
            UpdateStatus($"Benchmark {completed}/{update.Cases.Count}: {update.Cases.FirstOrDefault(x => x.Status == BenchmarkCaseStatus.Running)?.Label ?? update.Status.ToString()}");
            RefreshLogs();
        }), benchmarkCancellation.Token, saveHtml: false);
        var selectedContext = await PickContextForCacheLayer(benchmark);
        if (selectedContext is null)
        {
            app.Invoke(() => UpdateStatus($"Context benchmark {benchmark.Status}. Reports: {benchmark.HtmlReport}"));
            return;
        }

        var cacheBenchmark = new BenchmarkRecord
        {
            BenchmarkId = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{ProfileStore.Slugify(profile.Name)}_cache",
            ProfileName = profile.Name,
            BaselineProfile = selectedContext.Copy(selectedContext.Name),
            StartedAt = DateTimeOffset.Now,
            Workload = setup.Workload,
            OomPolicy = setup.OomPolicy,
            RunMathSuite = true,
            Cases = BenchmarkCases.GenerateCacheLayer(selectedContext)
        };
        activeBenchmark = cacheBenchmark;
        app.Invoke(() => UpdateStatus($"Cache and math benchmark started: 0/{cacheBenchmark.Cases.Count} cases. Press B to cancel."));
        await benchmarkRunner.RunAsync(cacheBenchmark, update => app.Invoke(() =>
        {
            var completed = update.Cases.Count(x => x.Status is not BenchmarkCaseStatus.Pending and not BenchmarkCaseStatus.Running);
            UpdateStatus($"Cache/math benchmark {completed}/{update.Cases.Count}: {update.Cases.FirstOrDefault(x => x.Status == BenchmarkCaseStatus.Running)?.Label ?? update.Status.ToString()}");
            RefreshLogs();
        }), benchmarkCancellation.Token, saveHtml: false);
        var combinedHtmlReport = BenchmarkReport.SaveCombinedHtml(cfg.BenchmarksDir, benchmark, cacheBenchmark);
        benchmark.HtmlReport = combinedHtmlReport;
        cacheBenchmark.HtmlReport = combinedHtmlReport;
        BenchmarkStore.SaveJson(cfg.BenchmarksDir, benchmark);
        BenchmarkStore.SaveJson(cfg.BenchmarksDir, cacheBenchmark);
        app.Invoke(() =>
        {
            UpdateStatus($"Cache and math benchmark {cacheBenchmark.Status}. Reports: {cacheBenchmark.HtmlReport}");
            ShowBenchmarkResults(app, cacheBenchmark);
        });
    }
    catch (Exception ex) { app.Invoke(() => UpdateStatus($"Benchmark failed: {ex.Message}")); }
    finally
    {
        benchmarkCancellation?.Dispose(); benchmarkCancellation = null;
        benchmarkActive = false;
        activeBenchmark = null;
        app.Invoke(RefreshLogs);
    }
}

Task<Profile?> PickContextForCacheLayer(BenchmarkRecord benchmark)
{
    var completion = new TaskCompletionSource<Profile?>();
    app.Invoke(() =>
    {
        ShowBenchmarkResults(app, benchmark, continuesToCacheAndMath: true);
        var candidates = benchmark.Cases.Where(x => x.Status == BenchmarkCaseStatus.Completed && x.Setting == "ctx").ToList();
        if (candidates.Count == 0)
        {
            MessageBox.ErrorQuery(app, "Cache benchmark", "No completed context cases are available for the cache layer.", "OK");
            completion.TrySetResult(null);
            return;
        }

        var dialog = new Window { Title = " Choose context for cache + math layer ", Width = 88, Height = 18 };
        dialog.Add(new Label { X = 2, Y = 1, Text = "Choose the completed context configuration to test with KV cache formats and the math suite." });
        var items = new ObservableCollection<string>(candidates.Select(x => $"{x.Label,-18}  {BenchmarkReport.Headroom(x),-30}  {BenchmarkReport.FormatVram(x)}"));
        var list = new ListView { X = 2, Y = 3, Width = Dim.Fill(4), Height = 9 };
        list.SetSource(items);
        var use = new Button { X = 2, Y = 13, Text = "Use selected context", IsDefault = true };
        var cancel = new Button { X = Pos.Right(use) + 2, Y = 13, Text = "Finish" };
        use.Accepting += (_, _) =>
        {
            var index = Math.Clamp(list.SelectedItem ?? 0, 0, candidates.Count - 1);
            var selected = candidates[index].Profile;
            completion.TrySetResult(selected.Copy(selected.Name));
            app.RequestStop();
        };
        cancel.Accepting += (_, _) => { completion.TrySetResult(null); app.RequestStop(); };
        dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { completion.TrySetResult(null); app.RequestStop(); key.Handled = true; } };
        dialog.Add(list, use, cancel);
        app.Run(dialog);
        completion.TrySetResult(null);
    });
    return completion.Task;
}

void CancelBenchmark()
{
    if (benchmarkCancellation is null) { UpdateStatus("No benchmark is running."); return; }
    benchmarkCancellation.Cancel();
    UpdateStatus("Cancelling benchmark and stopping its server…");
}

void CycleTheme()
{
    var themes = LltopTheme.Ids.ToList();
    var current = themes.FindIndex(x => x.Equals(LltopTheme.CurrentName, StringComparison.OrdinalIgnoreCase));
    var next = themes[(current + 1) % themes.Count];
    LltopTheme.Select(next);
    cfg.Theme = next;
    try { cfg.Save(); }
    catch (Exception ex) { UpdateStatus($"Theme changed for this session but could not save: {ex.Message}"); return; }
    ApplyLayout();
    RefreshProfileItems(runningProfile);
    RefreshLogs();
    UpdateStatus($"Theme changed to {LltopTheme.CurrentName}.");
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
    copy.SourcePath = "";
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

void ToggleFavorite()
{
    var profile = SelectedProfile();
    if (profile is null) { UpdateStatus("No profile selected."); return; }
    profile.Favorite = !profile.Favorite;
    try
    {
        store.Save(profile);
        ReloadProfiles(profile.Name, profile.Favorite ? $"Added {profile.Name} to favorites." : $"Removed {profile.Name} from favorites.");
    }
    catch (Exception ex) { UpdateStatus(ex.Message); }
}

bool IsCtrlF(Key key) => key.IsCtrl && key.NoCtrl.NoShift.KeyCode == KeyCode.F;

async Task Quit()
{
    if (closing) return;
    if (runner.IsActive)
    {
        var answer = MessageBox.Query(app, "Server is running", "Stop llama-server and quit lltop?", "Cancel", "Stop and quit");
        if (answer != 1) return;
    }
    closing = true;
    benchmarkCancellation?.Cancel();
    monitorCancellation.Cancel();
    await runner.StopAsync();
    app.RequestStop();
}

runner.LineReceived += line => app.Invoke(() =>
{
    serverStats.Consume(line, runner.StartedAt);
    lock (activeRunGate)
    {
        var timestamp = DateTimeOffset.Now;
        var graphEvent = LlamaRuntimeEventParser.Parse(line);
        if (graphEvent is not null) activeRunGraphData?.WriteEvent(timestamp, graphEvent);
        var telemetry = LlamaRuntimeEventParser.ParseGenerationTelemetry(line);
        if (telemetry is not null) activeRunGraphData?.WriteTelemetry(timestamp, telemetry, line);
    }
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
    if (profileList.SelectedItem is int value && profiles.Count > 0)
    {
        var favoriteCount = profiles.Count(p => p.Favorite);
        var visibleItem = UiText.SkipFavoritesDivider(value, selectedProfileListItem, profiles.Count, favoriteCount);
        if (visibleItem != value)
        {
            profileList.SelectedItem = visibleItem;
            return;
        }
        selectedProfileListItem = value;
        selected = Math.Clamp(value - (favoriteCount > 0 && value > favoriteCount ? 1 : 0), 0, profiles.Count - 1);
    }
    RefreshLogs(); UpdateStatus();
};
profileList.KeyDown += (_, key) =>
{
    if (!IsCtrlF(key)) return;
    ToggleFavorite();
    key.Handled = true;
};

app.Keyboard.KeyDown += (_, key) =>
{
    if (!ReferenceEquals(app.TopRunnableView, win)) return;
    var text = key.AsGrapheme;
    var showingLogs = runner.IsActive || externalServer is not null || logLines.Count > 0;
    if (logView.HasFocus && showingLogs && key.KeyCode is KeyCode.CursorUp or KeyCode.PageUp or KeyCode.Home)
    {
        PauseLogFollow();
        logScrollRow = key.KeyCode switch
        {
            KeyCode.Home => 0,
            KeyCode.PageUp => Math.Max(0, logScrollRow - 10),
            _ => Math.Max(0, logScrollRow - 1)
        };
        logView.ScrollTo(new System.Drawing.Point(0, logScrollRow));
        UpdateStatus($"Log follow paused at row {logScrollRow + 1}/{Math.Max(1, logLines.Count)}.");
        key.Handled = true;
    }
    else if (logView.HasFocus && !logAutoScroll && key.KeyCode is KeyCode.CursorUp or KeyCode.CursorDown or KeyCode.PageUp or KeyCode.PageDown or KeyCode.Home or KeyCode.End)
    {
        logScrollRow = key.KeyCode switch
        {
            KeyCode.PageUp => Math.Max(0, logScrollRow - 10),
            KeyCode.PageDown => Math.Min(Math.Max(0, logLines.Count - 1), logScrollRow + 10),
            KeyCode.Home => 0,
            KeyCode.CursorUp => Math.Max(0, logScrollRow - 1),
            KeyCode.CursorDown => Math.Min(Math.Max(0, logLines.Count - 1), logScrollRow + 1),
            _ => Math.Max(0, logLines.Count - 1)
        };
        if (logScrollRow >= Math.Max(0, logLines.Count - 1))
        {
            ResumeLogFollow();
            UpdateStatus("Log follow resumed at the bottom.");
        }
        else
        {
            logView.ScrollTo(new System.Drawing.Point(0, logScrollRow));
            UpdateLogStatus();
            UpdateStatus($"Log follow paused at row {logScrollRow + 1}/{Math.Max(1, logLines.Count)}.");
        }
        key.Handled = true;
    }
    else if (key.KeyCode == KeyCode.Enter) { _ = Launch(); key.Handled = true; }
    else if (text == "s") { _ = Stop(false); key.Handled = true; }
    else if (text == "K") { _ = Stop(true); key.Handled = true; }
    else if (key.KeyCode == (KeyCode.R | KeyCode.CtrlMask)) { RefreshModels(); key.Handled = true; }
    else if (IsCtrlF(key)) { ToggleFavorite(); key.Handled = true; }
    else if (text.Equals("r", StringComparison.OrdinalIgnoreCase)) { _ = Launch(true); key.Handled = true; }
    else if (text == "n") { NewProfile(); key.Handled = true; }
    else if (text.Equals("e", StringComparison.OrdinalIgnoreCase)) { EditSelected(); key.Handled = true; }
    else if (key.KeyCode == KeyCode.F2) { EditSelected(); key.Handled = true; }
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
    else if (text == "b") { _ = RunBenchmark(); key.Handled = true; }
    else if (text == "B") { CancelBenchmark(); key.Handled = true; }
    else if (text.Equals("t", StringComparison.OrdinalIgnoreCase)) { CycleTheme(); key.Handled = true; }
    else if (text.Equals("l", StringComparison.OrdinalIgnoreCase))
    {
        if (logAutoScroll) PauseLogFollow(); else ResumeLogFollow();
        UpdateStatus($"Log auto-scroll = {logAutoScroll}."); key.Handled = true;
    }
    else if (text.Equals("g", StringComparison.OrdinalIgnoreCase))
    {
        showingResourceGraph = !showingResourceGraph;
        RefreshLogs();
        key.Handled = true;
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
var startupMessage = !knownTheme ? $"Unknown theme '{cfg.Theme}'; using Midnight." : removedLegacyStarter ? "Removed the obsolete empty starter profile." : cfg.LoadMessage;
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
                ResumeLogFollowWhenIdle();
                if (runner.IsActive || benchmarkActive) return;
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
            app.Invoke(() =>
            {
                resourceStrip.Snapshot = snapshot;
                lock (activeRunGate)
                {
                    if (runner.IsActive && activeProfile is not null)
                    {
                        var sample = RunResourceSample.From(snapshot);
                        activeRunSamples.Add(sample);
                        activeRunGraphData?.WriteSample(sample);
                        if (activeRunSamples.Count > 1800) activeRunSamples.RemoveAt(0);
                    }
                }
                if (showingResourceGraph) RefreshLogs();
            });
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
    var editorOpen = true;
    var dialog = new Window { Title = $" {title} ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var fields = new Dictionary<string, TextField>();
    TextField Field(View page, string key, string label, string value, int x, int y, int width)
    {
        page.Add(new Label { X = x, Y = y, Text = label });
        var field = new TextField { X = x + label.Length + 1, Y = y, Width = width, Text = value };
        page.Add(field); fields[key] = field; return field;
    }
    string T(string key) => fields[key].Text;

    var tabs = new Tabs { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3) };
    var general = new View { Title = " General ", ViewportSettings = ViewportSettingsFlags.HasScrollBars };
    general.SetContentSize(new System.Drawing.Size(100, 27));
    var reasoningPage = new View { Title = " Reasoning ", ViewportSettings = ViewportSettingsFlags.HasScrollBars };
    reasoningPage.SetContentSize(new System.Drawing.Size(100, 34));
    var flash = new View { Title = " Flash-attn " };
    var mtpPage = new View { Title = " MTP " };
    tabs.Add(general, reasoningPage, flash, mtpPage);
    dialog.Add(tabs);

    general.Add(new Label { X = 2, Y = 1, Text = "Model, server, sampling, template, vision, and metadata" });
    var name = Field(general, "name", "Name", profile.Name, 2, 3, 30);
    Field(general, "description", "Description", profile.Description, 46, 3, 38);
    Field(general, "model", "Model", profile.Model, 2, 5, 86);
    Field(general, "server", "Server", profile.LlamaServer, 2, 7, 34);
    Field(general, "alias", "Alias", profile.Alias, 50, 7, 30);
    Field(general, "host", "Host", profile.Host, 2, 9, 16); Field(general, "port", "Port", profile.Port.ToString(), 29, 9, 6);
    Field(general, "ctx", "Context", profile.Ctx.ToString(), 46, 9, 8); Field(general, "ngl", "GPU layers", profile.Ngl.ToString(), 67, 9, 5);
    Field(general, "template", "Template", profile.ChatTemplate, 2, 11, 25);
    Field(general, "temp", "Temperature", profile.Temp.ToString(CultureInfo.InvariantCulture), 2, 13, 7); Field(general, "topP", "Top-P", profile.TopP.ToString(CultureInfo.InvariantCulture), 25, 13, 7); Field(general, "topK", "Top-K", profile.TopK.ToString(), 43, 13, 7); Field(general, "minP", "Min-P", profile.MinP.ToString(CultureInfo.InvariantCulture), 61, 13, 7);
    Field(general, "repeatPenalty", "Repeat penalty", profile.RepeatPenalty.ToString(CultureInfo.InvariantCulture), 2, 15, 7); Field(general, "repeatLastN", "Repeat last N", profile.RepeatLastN.ToString(), 30, 15, 7); Field(general, "presence", "Presence", profile.PresencePenalty.ToString(CultureInfo.InvariantCulture), 54, 15, 7); Field(general, "frequency", "Frequency", profile.FrequencyPenalty.ToString(CultureInfo.InvariantCulture), 74, 15, 7);
    var mmproj = Field(general, "mmproj", "Vision projector", profile.Mmproj, 2, 17, 65);
    var findMmproj = new Button { X = 83, Y = 17, Text = "Find sibling" };
    general.Add(findMmproj);
    Field(general, "imageTokens", "Image min tokens", profile.ImageMinTokens.ToString(), 2, 19, 7);
    Field(general, "extra", "Extra args", ArgumentText.Format(profile.ExtraArgs), 2, 21, 81);
    Field(general, "tags", "Tags", string.Join(", ", profile.Tags), 2, 23, 87);
    var vision = new CheckBox { X = 2, Y = 25, Text = "Use vision", Value = profile.Vision ? CheckState.Checked : CheckState.UnChecked };
    var jinja = new CheckBox { X = 20, Y = 25, Text = "Jinja", Value = profile.Jinja ? CheckState.Checked : CheckState.UnChecked };
    var metrics = new CheckBox { X = 34, Y = 25, Text = "Metrics", Value = profile.Metrics ? CheckState.Checked : CheckState.UnChecked };
    var mmap = new CheckBox { X = 50, Y = 25, Text = "Disable mmap", Value = profile.NoMmap ? CheckState.Checked : CheckState.UnChecked };
    general.Add(vision, jinja, metrics, mmap);

    reasoningPage.Add(new Label { X = 2, Y = 1, Text = "Thinking mode, effort, and token budget" });
    Field(reasoningPage, "reasoning", "Reasoning (auto/on/off)", profile.Reasoning, 2, 3, 9);
    reasoningPage.Add(new Label { X = 2, Y = 4, Text = "auto: follow the chat template. on/off: request thinking enabled/disabled." });
    reasoningPage.Add(new Label { X = 2, Y = 5, Text = "Requires model/template support. Blank leaves the server default." });
    Field(reasoningPage, "effort", "Effort", profile.ReasoningEffort, 2, 7, 12);
    var chooseEffort = new Button { X = 25, Y = 7, Text = "Choose effort...", Enabled = false };
    var useMedium = new Button { X = 48, Y = 7, Text = "Use Medium", Visible = false };
    reasoningPage.Add(chooseEffort, useMedium);
    reasoningPage.Add(new Label { X = 2, Y = 8, Text = "Blank/default: template default. Supported levels depend on the model/template." });
    reasoningPage.Add(new Label { X = 2, Y = 9, Text = "Analyze GGUF to unlock choices for this model. You can also enter an effort manually." });
    reasoningPage.Add(new Label { X = 2, Y = 10, Text = "Low favors speed; higher effort favors depth. Budget sets the thinking ceiling." });
    Field(reasoningPage, "budget", "Thinking budget (tokens)", profile.ReasoningBudget.ToString(), 2, 12, 9);
    reasoningPage.Add(new Label { X = 2, Y = 13, Text = "-1: no thinking cap. 0: end thinking immediately. Positive: thinking token cap." });
    reasoningPage.Add(new Label { X = 2, Y = 14, Text = "Example: 8000 caps thinking, then the answer continues. Output/context limits apply." });
    reasoningPage.Add(new Label { X = 2, Y = 16, Text = "Style (explore alternatives, check assumptions, be brief): use your client's system prompt." });
    var analyzeGguf = new Button { X = 2, Y = 18, Text = "Analyze GGUF" };
    var reasoningAnalysis = new Label { X = 2, Y = 20, Width = 96, Height = 13, Text = "Not sure which effort to use? Analyze GGUF to find choices for your model.\nYour current mode, effort, and budget will be kept." };
    var technicalDetails = new Button { X = 23, Y = 18, Text = "Technical details", Enabled = false };
    reasoningPage.Add(analyzeGguf, technicalDetails, reasoningAnalysis);
    GgufMetadata? analyzedMetadata = null;
    string analyzedInputs = "";
    string AnalysisInputs() => string.Join('\n', T("model"), T("template"), T("extra"), jinja.Value.ToString());
    bool AnalysisIsCurrent()
    {
        if (analyzedMetadata is not null && analyzedInputs == AnalysisInputs()) return true;
        reasoningAnalysis.Text = "Model or template settings changed. Analyze GGUF again to refresh the choices.";
        chooseEffort.Enabled = false;
        useMedium.Visible = false;
        return false;
    }
    chooseEffort.Accepting += (_, _) =>
    {
        if (!AnalysisIsCurrent()) return;
        var values = new[] { "" }.Concat(ReasoningAnalysis.EffortLevels(analyzedMetadata!)).ToArray();
        var labels = values.Select(value => value switch
        {
            "" => "Default", "medium" => "Medium (balanced)", "low" => "Low (quicker)",
            _ => char.ToUpperInvariant(value[0]) + value[1..]
        }).Append("Cancel").ToArray();
        var selected = MessageBox.Query(app, "Choose reasoning effort", "Choose the effort for this model. Default follows its template.\nYour thinking budget will stay unchanged. Press Save to keep the choice.", labels) ?? -1;
        if (selected >= 0 && selected < values.Length)
        {
            fields["effort"].Text = values[selected];
            reasoningAnalysis.Text = ReasoningAnalysis.Guidance(analyzedMetadata!, values[selected]);
        }
    };
    useMedium.Accepting += (_, _) =>
    {
        if (!AnalysisIsCurrent()) return;
        fields["effort"].Text = "medium";
        reasoningAnalysis.Text = "Medium selected. Your thinking budget is unchanged. Press Save to keep this setting.";
    };
    technicalDetails.Accepting += (_, _) =>
    {
        if (AnalysisIsCurrent()) MessageBox.Query(app, "Reasoning technical details", ReasoningAnalysis.Describe(analyzedMetadata!) + "\nEffort uses --reasoning-effort, or template parameters on older server builds.", "OK");
    };
    analyzeGguf.Accepting += async (_, _) =>
    {
        var modelPath = AppConfig.Expand(T("model").Trim());
        var hasOverride = !string.IsNullOrWhiteSpace(T("template")) || T("extra").Contains("--chat-template", StringComparison.Ordinal);
        var usesJinja = jinja.Value == CheckState.Checked;
        var inputSnapshot = AnalysisInputs();
        analyzeGguf.Enabled = false;
        chooseEffort.Enabled = false;
        useMedium.Visible = false;
        technicalDetails.Enabled = false;
        reasoningAnalysis.Text = "Reading GGUF metadata...";
        try
        {
            var metadata = await Task.Run(() => GgufMetadataReader.Read(modelPath));
            app.Invoke(() =>
            {
                if (!editorOpen) return;
                analyzedMetadata = metadata;
                analyzedInputs = inputSnapshot;
                if (!AnalysisIsCurrent()) return;
                technicalDetails.Enabled = true;
                reasoningAnalysis.Text = hasOverride
                    ? "A custom template is configured in General.\nThe GGUF cannot confirm its effort choices. Keep your settings or inspect Technical details."
                    : !usesJinja
                        ? "Enable Jinja in General, then analyze again to get choices from the model's template.\nYour current settings have been kept."
                        : ReasoningAnalysis.Guidance(metadata, T("effort"));
                var levels = ReasoningAnalysis.EffortLevels(metadata);
                chooseEffort.Enabled = !hasOverride && usesJinja && levels.Count > 0;
                useMedium.Visible = chooseEffort.Enabled && levels.Contains("medium");
            });
        }
        catch (Exception ex) { app.Invoke(() => { if (editorOpen) reasoningAnalysis.Text = $"Could not analyze GGUF: {ex.Message}"; }); }
        finally { app.Invoke(() => { if (editorOpen) analyzeGguf.Enabled = true; }); }
    };

    flash.Add(new Label { X = 2, Y = 1, Text = "Attention, KV cache, and server batching" });
    Field(flash, "flash", "Flash attention (auto/on/off)", profile.FlashAttn, 2, 4, 10);
    Field(flash, "cacheK", "KV cache K", profile.CacheK, 2, 6, 12); Field(flash, "cacheV", "KV cache V", profile.CacheV, 33, 6, 12);
    flash.Add(new Label { X = 2, Y = 8, Text = "q8_0 / q8_0: recommended quality baseline; uses about half the KV memory of f16 / f16." });
    flash.Add(new Label { X = 2, Y = 9, Text = "q4_0 / q4_0: about half q8 KV memory; use it to fit more context, then check quality. q4_0 / q8_0 is a middle ground." });
    Field(flash, "batch", "Batch", profile.Batch.ToString(), 2, 12, 8); Field(flash, "ubatch", "Micro batch", profile.UBatch.ToString(), 24, 12, 8); Field(flash, "parallel", "Parallel slots", profile.Parallel.ToString(), 49, 12, 6); Field(flash, "threads", "CPU threads", profile.Threads.ToString(), 72, 12, 6);
    Field(flash, "checkpoints", "Context checkpoints", profile.CtxCheckpoints.ToString(), 2, 15, 6);
    flash.Add(new Label { X = 2, Y = 18, Text = "Blank cache or flash values leave the matching llama.cpp option out of the launch command." });

    mtpPage.Add(new Label { X = 2, Y = 1, Text = "Multi-token prediction / draft-MTP" });
    Field(mtpPage, "mtp", "MTP (on/off)", profile.Mtp ? "on" : "off", 2, 4, 7);
    Field(mtpPage, "mtpTokens", "Draft tokens", profile.MtpDraftTokens.ToString(), 2, 6, 7);
    mtpPage.Add(new Label { X = 2, Y = 9, Text = "When on, lltop adds: --spec-type draft-mtp --spec-draft-n-max <draft tokens>" });
    mtpPage.Add(new Label { X = 2, Y = 11, Text = "When off, draft tokens should be 0. When on, draft tokens must be at least 1." });

    var message = new Label { X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(28), Text = "Select a tab with its header, Ctrl+Tab, or Ctrl+PageUp/PageDown." };
    var defaults = new Button { X = Pos.AnchorEnd(37), Y = Pos.AnchorEnd(1), Text = "Default settings" };
    var save = new Button { X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
    var cancel = new Button { X = Pos.Right(save) + 1, Y = Pos.AnchorEnd(1), Text = "Cancel" };
    dialog.Add(message, defaults, save, cancel);
    var accepted = false;
    findMmproj.Accepting += (_, _) =>
    {
        var match = VisionProjectorResolver.FindBeside(AppConfig.Expand(T("model")));
        if (match.Path is not null) mmproj.Text = match.Path;
        message.Text = match.Message;
    };
    defaults.Accepting += (_, _) =>
    {
        var defaultsProfile = profile.Copy(profile.Name);
        defaultsProfile.ApplyRecommendedSettings();
        fields["ctx"].Text = defaultsProfile.Ctx.ToString(); fields["ngl"].Text = defaultsProfile.Ngl.ToString();
        fields["temp"].Text = defaultsProfile.Temp.ToString(CultureInfo.InvariantCulture); fields["topP"].Text = defaultsProfile.TopP.ToString(CultureInfo.InvariantCulture); fields["topK"].Text = defaultsProfile.TopK.ToString(); fields["minP"].Text = defaultsProfile.MinP.ToString(CultureInfo.InvariantCulture);
        fields["repeatPenalty"].Text = defaultsProfile.RepeatPenalty.ToString(CultureInfo.InvariantCulture); fields["repeatLastN"].Text = defaultsProfile.RepeatLastN.ToString(); fields["presence"].Text = defaultsProfile.PresencePenalty.ToString(CultureInfo.InvariantCulture); fields["frequency"].Text = defaultsProfile.FrequencyPenalty.ToString(CultureInfo.InvariantCulture);
        fields["flash"].Text = defaultsProfile.FlashAttn; fields["cacheK"].Text = defaultsProfile.CacheK; fields["cacheV"].Text = defaultsProfile.CacheV;
        fields["batch"].Text = defaultsProfile.Batch.ToString(); fields["ubatch"].Text = defaultsProfile.UBatch.ToString(); fields["parallel"].Text = defaultsProfile.Parallel.ToString(); fields["threads"].Text = defaultsProfile.Threads.ToString(); fields["checkpoints"].Text = defaultsProfile.CtxCheckpoints.ToString();
        fields["reasoning"].Text = defaultsProfile.Reasoning; fields["budget"].Text = defaultsProfile.ReasoningBudget.ToString(); fields["mtp"].Text = "off"; fields["mtpTokens"].Text = "0";
        fields["effort"].Text = defaultsProfile.ReasoningEffort;
        jinja.Value = defaultsProfile.Jinja ? CheckState.Checked : CheckState.UnChecked; metrics.Value = defaultsProfile.Metrics ? CheckState.Checked : CheckState.UnChecked; mmap.Value = defaultsProfile.NoMmap ? CheckState.Checked : CheckState.UnChecked;
        message.Text = "Recommended settings restored: Q8 KV cache, 64K context, conservative sampling. Model and connection details were kept.";
    };
    save.Accepting += (_, _) =>
    {
        try
        {
            profile.Name = name.Text.Trim(); profile.Description = T("description").Trim(); profile.Model = AppConfig.Expand(T("model")); profile.LlamaServer = AppConfig.Expand(T("server")); profile.Alias = T("alias").Trim(); profile.Host = T("host").Trim(); profile.Port = ParseInt(T("port"), "Port");
            profile.Ctx = ParseInt(T("ctx"), "Context"); profile.Ngl = ParseInt(T("ngl"), "GPU layers"); profile.ChatTemplate = T("template").Trim(); profile.Reasoning = T("reasoning").Trim().ToLowerInvariant(); profile.ReasoningBudget = ParseInt(T("budget"), "Reasoning budget");
            profile.ReasoningEffort = T("effort").Trim().ToLowerInvariant();
            profile.Temp = ParseDouble(T("temp"), "Temperature"); profile.TopP = ParseDouble(T("topP"), "Top P"); profile.TopK = ParseInt(T("topK"), "Top K"); profile.MinP = ParseDouble(T("minP"), "Min P"); profile.RepeatPenalty = ParseDouble(T("repeatPenalty"), "Repeat penalty"); profile.RepeatLastN = ParseInt(T("repeatLastN"), "Repeat last N"); profile.PresencePenalty = ParseDouble(T("presence"), "Presence penalty"); profile.FrequencyPenalty = ParseDouble(T("frequency"), "Frequency penalty");
            profile.Vision = vision.Value == CheckState.Checked; profile.Mmproj = AppConfig.Expand(T("mmproj")); profile.ImageMinTokens = ParseInt(T("imageTokens"), "Image minimum tokens");
            if (profile.Vision && string.IsNullOrWhiteSpace(profile.Mmproj)) { var match = VisionProjectorResolver.FindBeside(profile.Model); if (match.Path is null) throw new InvalidOperationException(match.Message); profile.Mmproj = match.Path; }
            profile.ExtraArgs = ArgumentText.Parse(T("extra")); profile.Tags = T("tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); profile.Jinja = jinja.Value == CheckState.Checked; profile.Metrics = metrics.Value == CheckState.Checked; profile.NoMmap = mmap.Value == CheckState.Checked;
            profile.FlashAttn = T("flash").Trim().ToLowerInvariant(); profile.CacheK = T("cacheK").Trim(); profile.CacheV = T("cacheV").Trim(); profile.Batch = ParseInt(T("batch"), "Batch"); profile.UBatch = ParseInt(T("ubatch"), "Micro batch"); profile.Parallel = ParseInt(T("parallel"), "Parallel slots"); profile.Threads = ParseInt(T("threads"), "CPU threads"); profile.CtxCheckpoints = ParseInt(T("checkpoints"), "Context checkpoints");
            profile.Mtp = ParseOnOff(T("mtp"), "MTP"); profile.MtpDraftTokens = ParseInt(T("mtpTokens"), "MTP draft tokens");
            profile.Validate(); accepted = true; app.RequestStop();
        }
        catch (Exception ex) { message.Text = ex.Message; }
    };
    cancel.Accepting += (_, _) => app.RequestStop();
    try { app.Run(dialog); }
    finally { editorOpen = false; }
    return accepted;
}


static int ParseInt(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new FormatException($"{name} must be a whole number.");
static bool ParseOnOff(string value, string name) => value.Trim().ToLowerInvariant() switch
{
    "on" => true,
    "off" => false,
    _ => throw new FormatException($"{name} must be on or off.")
};
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
    var supportsVision = VisionProjectorResolver.SupportsModel(profile.Model);
    var projector = supportsVision ? VisionProjectorResolver.FindBeside(profile.Model) : null;
    var visionStatus = profile.Vision && File.Exists(profile.Mmproj)
        ? $"Ready — {Path.GetFileName(profile.Mmproj)}"
        : supportsVision && projector?.Path is not null
            ? $"Projector found — {Path.GetFileName(projector.Path)}"
            : supportsVision ? "No matching projector found (text-only launch is available)" : "Not available for this model";
    var modelsFound = "unavailable";
    try { modelsFound = FirstRunProfiles.DiscoverModels(cfg.ModelsDir).Count.ToString(CultureInfo.InvariantCulture); }
    catch { }
    var discovery = modelsFound == "unavailable"
        ? "Discovery    unavailable"
        : $"Discovery    {modelsFound} runnable model{(modelsFound == "1" ? "" : "s")} found";
    var assessment = isDiffusion ? "Needs attention" : supportsVision && !profile.Vision ? "Ready for text-only launch" : "Ready to launch";
    var advice = isDiffusion
        ? "⚠ This is an experimental diffusion-style GGUF. Select a diffusion-enabled llama-server runtime before launching, unless you intend to test this runtime."
        : "No known compatibility issue was detected. This configuration has not been run yet.";
    var report = $"New configuration — no previous run.\n\n" +
                 $"Model        {Path.GetFileName(profile.Model)}\n" +
                 $"Architecture {architecture}\n\n" +
                 $"{discovery}\n\n" +
                 $"Vision       {visionStatus}\n\n" +
                 $"Detected runtime\n" +
                 $"Runtime      {suggestedRuntime}\n" +
                 $"Build        llama.cpp {suggestedCapability.BuildSummary}\n" +
                 $"Backend      {suggestedBackend}\n" +
                 $"Path         {suggestedRuntimePath}\n" +
                 $"GPU layers   {profile.Ngl} requested\n\n" +
                 $"{(capability.BinaryExists ? "✅" : "❌")} Runtime file {(capability.BinaryExists ? "found" : "missing")}\n" +
                 $"{(capability.VersionProbeSucceeded ? "✅" : "⚠")} Runtime version probe {(capability.VersionProbeSucceeded ? "succeeds" : "did not succeed")}\n" +
                 $"{(capability.HelpProbeSucceeded ? "✅" : "⚠")} Runtime option probe {(capability.HelpProbeSucceeded ? "succeeds" : "did not succeed")}" +
                 (usesSuggestedRuntime ? "" : $"\n⚠ Profile uses {Path.GetFileName(selectedRuntimePath)} instead of the suggested runtime.") +
                 $"\n\nLaunch assessment: {assessment}\n" +
                 advice;
    var window = new Window { Title = " First launch advisor ", Width = Dim.Percent(80), Height = 30 };
    window.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { app.RequestStop(); key.Handled = true; } };
    window.Add(new Label { X = 1, Y = 1, Width = Dim.Fill(2), Height = 22, Text = report });
    var action = FirstLaunchAction.Cancel;
    var cancel = new Button { X = 1, Y = 25, Text = "Cancel" };
    Button? vision = supportsVision ? new Button { X = Pos.Right(cancel) + 2, Y = 25, Text = "Set up vision" } : null;
    var edit = new Button { X = vision is null ? Pos.Right(cancel) + 2 : Pos.Right(vision) + 2, Y = 25, Text = "Edit profile" };
    var preview = new Button { X = Pos.Right(edit) + 2, Y = 25, Text = "Preview command" };
    var launch = new Button { X = Pos.Right(preview) + 2, Y = 25, Text = "Launch" };
    cancel.Accepting += (_, _) => app.RequestStop();
    if (vision is not null) vision.Accepting += (_, _) => { action = FirstLaunchAction.SetupVision; app.RequestStop(); };
    edit.Accepting += (_, _) => { action = FirstLaunchAction.Edit; app.RequestStop(); };
    preview.Accepting += (_, _) => { action = FirstLaunchAction.Preview; app.RequestStop(); };
    launch.Accepting += (_, _) => { action = FirstLaunchAction.Launch; app.RequestStop(); };
    if (vision is null) window.Add(cancel, edit, preview, launch);
    else window.Add(cancel, vision, edit, preview, launch);
    launch.SetFocus();
    app.Run(window);
    return action;
}

static bool ShowVisionSetup(IApplication app, Profile profile)
{
    var supportsVision = VisionProjectorResolver.SupportsModel(profile.Model);
    var match = supportsVision ? VisionProjectorResolver.FindBeside(profile.Model) : null;
    var dialog = new Window { Title = " Vision setup ", Width = 92, Height = 22 };
    var modelFolder = Path.GetDirectoryName(Path.GetFullPath(profile.Model)) ?? "unknown";
    var message = new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 7 };
    var projectorPath = new TextField { X = 2, Y = 10, Width = Dim.Fill(4), Text = match?.Path ?? profile.Mmproj };
    var refresh = new Button { X = 2, Y = 13, Text = "Refresh scan" };
    var use = new Button { X = Pos.Right(refresh) + 2, Y = 13, Text = "Use projector" };
    var withoutVision = new Button { X = Pos.Right(use) + 2, Y = 13, Text = "Continue without vision" };
    var back = new Button { X = Pos.Right(withoutVision) + 2, Y = 13, Text = "Back" };
    var changed = false;

    void Describe(string? scanMessage = null)
    {
        message.Text = supportsVision
            ? $"This model can accept images with a matching vision projector.\n\nModel: {Path.GetFileName(profile.Model)}\nFolder: {modelFolder}\n\nPut {VisionProjectorResolver.ExpectedProjectorName} from this model family in that folder, then refresh.\n{scanMessage ?? match?.Message ?? "No projector has been selected."}"
            : $"Vision setup is not available for {Path.GetFileName(profile.Model)}.\n\nlltop currently supports vision for Qwen3.6-35B-A3B and Qwen3.8-27B model GGUFs with\n{VisionProjectorResolver.ExpectedProjectorName}. You can still launch this model for text chat.";
    }

    Describe();
    dialog.Add(message, new Label { X = 2, Y = 8, Text = "Projector path (paste or edit)" }, projectorPath, refresh, use, withoutVision, back);
    refresh.Accepting += (_, _) =>
    {
        match = supportsVision ? VisionProjectorResolver.FindBeside(profile.Model) : null;
        if (match?.Path is not null) projectorPath.Text = match.Path;
        Describe(match?.Message);
    };
    use.Accepting += (_, _) =>
    {
        try
        {
            if (!supportsVision) throw new InvalidOperationException("This model is not supported for vision setup.");
            var path = AppConfig.Expand(projectorPath.Text.Trim());
            if (!File.Exists(path)) throw new FileNotFoundException("Projector file was not found.", path);
            var candidate = profile.Copy(profile.Name);
            candidate.Vision = true;
            candidate.Mmproj = path;
            if (candidate.ImageMinTokens == 0) candidate.ImageMinTokens = 1024;
            candidate.Validate();
            profile.Vision = candidate.Vision;
            profile.Mmproj = candidate.Mmproj;
            profile.ImageMinTokens = candidate.ImageMinTokens;
            changed = true;
            app.RequestStop();
        }
        catch (Exception ex) { Describe(ex.Message); }
    };
    withoutVision.Accepting += (_, _) =>
    {
        changed = profile.Vision || !string.IsNullOrWhiteSpace(profile.Mmproj);
        profile.Vision = false;
        profile.Mmproj = "";
        app.RequestStop();
    };
    back.Accepting += (_, _) => app.RequestStop();
    dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { app.RequestStop(); key.Handled = true; } };
    app.Run(dialog);
    return changed;
}

static bool RunFirstRunWizard(IApplication app, AppConfig cfg)
{
    var wizard = new Window { Title = " Setup · step 1 of 2 ", Width = 90, Height = 19 };
    wizard.Add(new Label { X = 2, Y = 1, Text = "Step 1: connect lltop to your llama.cpp installation. Next, you will review the models found." });
    wizard.Add(new Label { X = 2, Y = 3, Text = "llama-server binary or app directory" });
    var server = new TextField { X = 2, Y = 4, Width = Dim.Fill(4), Text = "~/llama/app" };
    wizard.Add(server, new Label { X = 2, Y = 6, Text = "Models directory" });
    var models = new TextField { X = 2, Y = 7, Width = Dim.Fill(4), Text = "~/llama/models" };
    var message = new Label { X = 2, Y = 10, Width = Dim.Fill(4), Height = 2, Text = "Both paths must already exist. Esc cancels setup." };
    var save = new Button { X = 2, Y = 14, Text = "Next: review models", IsDefault = true };
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
            var inspection = FirstRunProfiles.InspectModels(modelsPath).Where(model => model.IsRunnable).ToList();
            if (!ShowFirstRunModelReview(app, modelsPath, inspection)) return;
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

static bool ShowFirstRunModelReview(IApplication app, string modelsPath, IReadOnlyList<ModelInspection> models)
{
    var review = new Window { Title = " Setup · step 2 of 2 · review models ", Width = 100, Height = 24 };
    var runnable = models.Count(model => model.IsRunnable);
    review.Add(new Label
    {
        X = 2, Y = 1, Width = Dim.Fill(4),
        Text = $"Step 2: review the verified GGUF language models before entering lltop. Found {runnable} model{(runnable == 1 ? "" : "s")} in {modelsPath}."
    });
    review.Add(new Label { X = 2, Y = 3, Text = "Only verified GGUF language models are shown and will receive profiles." });
    var items = new ObservableCollection<string>(models.Select(model =>
        $"{(model.IsRunnable ? "✓" : "!")} {Path.GetFileName(model.Path),-42}  {model.Status}"));
    if (items.Count == 0) items.Add("No verified GGUF language models found.");
    var list = new ListView { X = 2, Y = 5, Width = Dim.Fill(4), Height = 11 };
    list.SetSource(items);
    var continueButton = new Button { X = 2, Y = 18, Text = "Create profiles and continue", IsDefault = true };
    var back = new Button { X = Pos.Right(continueButton) + 2, Y = 18, Text = "Back" };
    var confirmed = false;
    continueButton.Accepting += (_, _) => { confirmed = true; app.RequestStop(); };
    back.Accepting += (_, _) => app.RequestStop();
    review.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { app.RequestStop(); key.Handled = true; } };
    review.Add(list, continueButton, back);
    app.Run(review);
    return confirmed;
}

static BenchmarkSetup? ShowBenchmarkSetup(IApplication app, Profile profile)
{
    BenchmarkSetup? setup = null;
    var dialog = new Window { Title = $" Benchmark setup · {profile.Name} ", Width = 92, Height = 16 };
    dialog.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { app.RequestStop(); key.Handled = true; } };
    dialog.Add(new Label { X = 2, Y = 1, Text = "Context start" });
    var startContext = new TextField { X = 16, Y = 1, Width = 10, Text = Math.Max(1, profile.Ctx / 2).ToString(CultureInfo.InvariantCulture) };
    dialog.Add(new Label { X = 31, Y = 1, Text = "stop" });
    var stopContext = new TextField { X = 37, Y = 1, Width = 10, Text = (profile.Ctx * 2).ToString(CultureInfo.InvariantCulture) };
    dialog.Add(new Label { X = 52, Y = 1, Text = "steps" });
    var contextSteps = new TextField { X = 59, Y = 1, Width = 5, Text = "5" };
    dialog.Add(startContext, stopContext, contextSteps);
    dialog.Add(new Label { X = 2, Y = 3, Text = "Steps include start and stop. The current profile is kept as a reference and is not run." });
    dialog.Add(new Label { X = 2, Y = 5, Text = "Warmup prompt" });
    var prompt = new TextField { X = 17, Y = 5, Width = Dim.Fill(4), Text = "Reply with a short benchmark acknowledgement." };
    dialog.Add(prompt);
    dialog.Add(new Label { X = 2, Y = 7, Text = "Max tokens" });
    var maxTokens = new TextField { X = 17, Y = 7, Width = 8, Text = "32" };
    var continueOom = new CheckBox { X = 30, Y = 7, Text = "Continue after OOM" };
    var message = new Label { X = 2, Y = 9, Width = Dim.Fill(4), Text = "Requires no active managed or external llama-server. Readiness timeout is 300 seconds." };
    var start = new Button { X = 2, Y = 12, Text = "Start benchmark", IsDefault = true };
    var cancel = new Button { X = Pos.Right(start) + 2, Y = 14, Text = "Cancel" };
    start.Accepting += (_, _) =>
    {
        try
        {
            if (!int.TryParse(maxTokens.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens)) throw new InvalidOperationException("Max tokens must be an integer.");
            var workload = new BenchmarkWorkload { Prompt = prompt.Text.Trim(), MaxTokens = tokens };
            workload.Validate();
            if (!int.TryParse(startContext.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum) || minimum < 1)
                throw new InvalidOperationException("Context start must be a positive integer.");
            if (!int.TryParse(stopContext.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum) || maximum < minimum)
                throw new InvalidOperationException("Context stop must be an integer no less than the start.");
            if (!int.TryParse(contextSteps.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps) || steps < 2)
                throw new InvalidOperationException("Context steps must be at least 2.");
            setup = new([new BenchmarkSweep { Setting = "ctx", Minimum = minimum.ToString(CultureInfo.InvariantCulture), Maximum = maximum.ToString(CultureInfo.InvariantCulture), Steps = steps }], workload, continueOom.Value == CheckState.Checked ? BenchmarkOomPolicy.Continue : BenchmarkOomPolicy.Stop);
            if (BenchmarkCases.Generate(profile, setup.Sweeps).Count == 0)
                throw new InvalidOperationException("This context sweep only contains the current profile's context. Choose a different range or more steps.");
            app.RequestStop();
        }
        catch (Exception ex) { message.Text = ex.Message; }
    };
    cancel.Accepting += (_, _) => app.RequestStop();
    dialog.Add(prompt, maxTokens, continueOom, message, start, cancel);
    app.Run(dialog);
    return setup;
}

static void ShowBenchmarkResults(IApplication app, BenchmarkRecord benchmark, bool continuesToCacheAndMath = false)
{
    var title = continuesToCacheAndMath
        ? $" Benchmark · phase 1 of 2 · context results · {benchmark.ProfileName} "
        : $" Benchmark results · {benchmark.ProfileName} ";
    var window = new Window { Title = title, Width = Dim.Percent(90), Height = Dim.Percent(80) };
    window.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc || key.AsGrapheme.Equals("q", StringComparison.OrdinalIgnoreCase)) { app.RequestStop(); key.Handled = true; } };
    var warnings = benchmark.Cases.Where(x => BenchmarkReport.Headroom(x).StartsWith("WARNING", StringComparison.Ordinal) || BenchmarkReport.Headroom(x).StartsWith("CRITICAL", StringComparison.Ordinal)).ToList();
    var peak = benchmark.Cases.Where(x => x.VramUsedBytes.HasValue).OrderByDescending(x => x.VramUsedBytes).FirstOrDefault();
    var lines = new List<string>();
    if (continuesToCacheAndMath)
        lines.AddRange(["Phase 1 of 2 complete. Next, choose a context result for the cache + math benchmark.", ""]);
    lines.AddRange([
        $"Status       {benchmark.Status}",
        $"Cases        {benchmark.Cases.Count(x => x.Status == BenchmarkCaseStatus.Completed)}/{benchmark.Cases.Count} completed",
        $"Memory fit   {BenchmarkReport.MemoryPosture(benchmark, peak)}",
        $"Peak VRAM    {(peak is null ? "unavailable" : $"{peak.Label} · {BenchmarkReport.FormatVram(peak)}")}",
        $"Risk         {(warnings.Count == 0 ? "No close-to-OOM cases detected." : $"{warnings.Count} close-to-OOM case(s) — see ! rows below.")}",
        "",
        "CASE                         STATUS        MATH     POST-WARMUP VRAM                 HEADROOM / RISK",
        new string('─', 92)
    ]);
    lines.AddRange(benchmark.Cases.Select(x => $"{x.Label,-28} {x.Status,-13} {(x.MathTotal is null ? "—" : $"{x.MathCorrect}/{x.MathTotal}"),-8} {BenchmarkReport.FormatVram(x),-34} {BenchmarkReport.Headroom(x)}{(x.Error.Length > 0 ? $"  {x.Error}" : "")}"));
    lines.AddRange(["", "Reports", $"JSON  {benchmark.JsonReport}"]);
    if (!string.IsNullOrWhiteSpace(benchmark.HtmlReport)) lines.Add($"HTML  {benchmark.HtmlReport}");
    lines.AddRange(["", "Close-to-OOM means peak sampled VRAM was at least 80% of reported total GPU VRAM."]);
    var results = new LogTextView { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true,
        Text = string.Join('\n', lines), HighlightSeverityMarkersOnly = true };
    LltopTheme.ApplyAnalysis(results);
    var hasHtmlReport = !string.IsNullOrWhiteSpace(benchmark.HtmlReport);
    var openReport = new Button { X = 1, Y = Pos.Bottom(results), Text = "Open HTML report", Visible = hasHtmlReport };
    openReport.Accepting += (_, _) =>
    {
        try { LaunchBenchmarkReport(benchmark.HtmlReport); }
        catch (Exception ex) { MessageBox.ErrorQuery(app, "Open benchmark report", ex.Message, "OK"); }
    };
    var close = new Button { X = hasHtmlReport ? Pos.Right(openReport) + 2 : 1, Y = Pos.Bottom(results), Text = continuesToCacheAndMath ? "Next: choose cache context" : "Close", IsDefault = true };
    close.Accepting += (_, _) => app.RequestStop();
    window.Add(results, openReport, close); app.Run(window);
}

static void LaunchBenchmarkReport(string reportPath)
{
    if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
        throw new FileNotFoundException("The generated HTML report was not found.", reportPath);
    ProcessStartInfo startInfo;
    if (OperatingSystem.IsWindows()) startInfo = new ProcessStartInfo { FileName = reportPath, UseShellExecute = true };
    else if (OperatingSystem.IsMacOS()) startInfo = new ProcessStartInfo { FileName = "open", UseShellExecute = false, ArgumentList = { reportPath } };
    else startInfo = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false, ArgumentList = { reportPath } };
    if (Process.Start(startInfo) is null) throw new InvalidOperationException("Could not launch the default browser.");
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

enum FirstLaunchAction { Cancel, SetupVision, Edit, Preview, Launch }
