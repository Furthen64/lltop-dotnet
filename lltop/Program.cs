using System.Collections.ObjectModel;
using System.Globalization;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var app = Application.Create().Init();
var cfg = AppConfig.Load();
if (cfg.IsFirstRun && !RunFirstRunWizard(app, cfg)) return;

var store = new ProfileStore(cfg.ProfilesDir);
var load = store.LoadAll();
var profiles = load.Profiles;
var selected = 0;
var runner = new ServerRunner();
var runningProfile = "";
var logLines = new List<string>();
var profileItems = new ObservableCollection<string>();
var closing = false;

var win = new Window { Title = " lltop · llama.cpp control center ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
var banner = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Text = "LLAMA SERVER  •  profiles, launches, and live output" };
var profileFrame = new FrameView { Title = " Profiles ", X = 0, Y = 2, Width = Dim.Percent(34), Height = Dim.Fill(11) };
var logFrame = new FrameView { Title = " Live log ", X = Pos.Right(profileFrame), Y = 2, Width = Dim.Fill(), Height = Dim.Fill(11) };
var profileList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
#pragma warning disable CS0618 // Terminal.Gui 2.4 ships TextView as its built-in scrollable read-only text control.
var logView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = false, Text = "Waiting for a server launch…" };
#pragma warning restore CS0618
profileFrame.Add(profileList); logFrame.Add(logView);
var statusFrame = new FrameView { Title = " Selected profile / server ", X = 0, Y = Pos.Bottom(profileFrame), Width = Dim.Fill(), Height = 8 };
var status = new Label { X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(), Text = "Loading…" };
statusFrame.Add(status);
var help = new Label { X = 1, Y = Pos.Bottom(statusFrame), Width = Dim.Fill(2), Height = 3,
    Text = "Enter start   s stop   K kill   r restart   n new   e edit   d duplicate   x delete\nF5 reload     v command preview     q quit" };
win.Add(banner, profileFrame, logFrame, statusFrame, help);

void RefreshProfileItems(string? selectName = null)
{
    profileItems.Clear();
    if (profiles.Count == 0) profileItems.Add("  No profiles yet — press n to create one");
    else foreach (var p in profiles)
    {
        var marker = p.Name.Equals(runningProfile, StringComparison.OrdinalIgnoreCase)
            ? runner.State == RunnerState.Running ? "●" : "◐" : "○";
        var model = string.IsNullOrWhiteSpace(p.Model) ? "model not set" : Path.GetFileName(p.Model);
        profileItems.Add($"{marker} {p.Name}  ·  {model}");
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
    var state = runner.State.ToString().ToUpperInvariant();
    var pid = runner.ProcessId is int id ? $"  PID {id}" : "";
    var uptime = runner.StartedAt is { } started && runner.IsActive ? $"  Uptime {(DateTimeOffset.Now - started):hh\\:mm\\:ss}" : "";
    if (p is null)
    {
        status.Text = $"STATE    {state}{pid}\n\nNo profiles found in {cfg.ProfilesDir}\n{message}";
        return;
    }
    var model = string.IsNullOrWhiteSpace(p.Model) ? "not configured" : p.Model;
    var description = string.IsNullOrWhiteSpace(p.Description) ? "—" : p.Description;
    status.Text = $"STATE    {state}{pid}{uptime}     PROFILE  {p.Name}     BIND  {p.Host}:{p.Port}\n" +
                  $"MODEL    {model}\n" +
                  $"DETAIL   ctx {p.Ctx:N0}  gpu layers {p.Ngl}  parallel {p.Parallel}  flash-attn {p.FlashAttn}\n" +
                  $"ABOUT    {description}" + (string.IsNullOrWhiteSpace(message) ? "" : $"\nINFO     {message}");
}

void RefreshLogs()
{
    logView.Text = logLines.Count == 0 ? "Waiting for server output…" : string.Join('\n', logLines);
    logView.MoveEnd();
}

void ReloadProfiles(string? selectName = null, string message = "Profiles reloaded.")
{
    var result = store.LoadAll();
    profiles = result.Profiles;
    RefreshProfileItems(selectName);
    var suffix = result.Errors.Count == 0 ? message : $"{message}  Skipped: {string.Join(" | ", result.Errors)}";
    UpdateStatus(suffix);
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
            UpdateStatus("Stopping the current server for restart…");
            await runner.StopAsync();
        }
        logLines.Clear(); RefreshLogs();
        runningProfile = profile.Name;
        await runner.StartAsync(cfg, profile);
        RefreshProfileItems(profile.Name);
        UpdateStatus($"Started successfully. Log: {runner.LogPath}");
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
    await runner.StopAsync();
    app.RequestStop();
}

runner.LineReceived += line => app.Invoke(() =>
{
    logLines.Add(line);
    if (logLines.Count > 500) logLines.RemoveAt(0);
    RefreshLogs();
});
runner.StateChanged += _ => app.Invoke(() => { RefreshProfileItems(runningProfile); UpdateStatus(); });
runner.Exited += exit => app.Invoke(() =>
{
    var name = runningProfile; runningProfile = ""; RefreshProfileItems(name);
    UpdateStatus(exit.Requested ? $"Server stopped (exit {exit.ExitCode})." : $"Server exited with code {exit.ExitCode}." + (exit.Error is null ? "" : $" {exit.Error}"));
});
profileList.ValueChanged += (_, _) =>
{
    if (profileList.SelectedItem is int value && profiles.Count > 0) selected = Math.Clamp(value, 0, profiles.Count - 1);
    UpdateStatus();
};

app.Keyboard.KeyDown += (_, key) =>
{
    var text = key.AsGrapheme;
    if (key.KeyCode == KeyCode.Enter) { _ = Launch(); key.Handled = true; }
    else if (text == "s") { _ = Stop(false); key.Handled = true; }
    else if (text == "K") { _ = Stop(true); key.Handled = true; }
    else if (text.Equals("r", StringComparison.OrdinalIgnoreCase)) { _ = Launch(true); key.Handled = true; }
    else if (text.Equals("n", StringComparison.OrdinalIgnoreCase)) { NewProfile(); key.Handled = true; }
    else if (text.Equals("e", StringComparison.OrdinalIgnoreCase)) { EditSelected(); key.Handled = true; }
    else if (text.Equals("d", StringComparison.OrdinalIgnoreCase)) { DuplicateSelected(); key.Handled = true; }
    else if (text.Equals("x", StringComparison.OrdinalIgnoreCase)) { DeleteSelected(); key.Handled = true; }
    else if (text.Equals("v", StringComparison.OrdinalIgnoreCase))
    {
        var p = SelectedProfile();
        UpdateStatus(p is null ? "No profile selected." : ServerRunner.BuildArguments(p).Aggregate("llama-server", (all, arg) => all + " " + arg));
        key.Handled = true;
    }
    else if (key.KeyCode == KeyCode.F5) { ReloadProfiles(); key.Handled = true; }
    else if (text.Equals("q", StringComparison.OrdinalIgnoreCase) || key.KeyCode == KeyCode.Esc) { _ = Quit(); key.Handled = true; }
};

RefreshProfileItems();
UpdateStatus(load.Errors.Count == 0 ? cfg.LoadMessage : $"Skipped invalid profiles: {string.Join(" | ", load.Errors)}");
app.Run(win);
runner.Dispose();

static bool EditProfile(IApplication app, Profile profile, string title)
{
    var dialog = new Window { Title = $" {title} ", Width = 96, Height = 31 };
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
    var jinja = new CheckBox { X = 2, Y = 23, Text = "Jinja", Value = profile.Jinja ? CheckState.Checked : CheckState.UnChecked };
    var metrics = new CheckBox { X = 20, Y = 23, Text = "Metrics", Value = profile.Metrics ? CheckState.Checked : CheckState.UnChecked };
    var mmap = new CheckBox { X = 40, Y = 23, Text = "Disable mmap", Value = profile.NoMmap ? CheckState.Checked : CheckState.UnChecked };
    dialog.Add(jinja, metrics, mmap);
    var message = new Label { X = 2, Y = 25, Width = Dim.Fill(2), Text = "Tab moves between fields. Paths may use ~ and environment variables." };
    var save = new Button { X = 2, Y = 27, Text = "Save", IsDefault = true };
    var cancel = new Button { X = Pos.Right(save) + 2, Y = 27, Text = "Cancel" };
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
            FirstRunProfiles.ScanAndGenerate(cfg);
            completed = true;
            app.RequestStop();
        }
        catch (Exception ex) { message.Text = ex.Message; }
    };
    cancel.Accepting += (_, _) => app.RequestStop();
    app.Run(wizard);
    return completed;
}
