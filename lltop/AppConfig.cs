sealed class AppConfig
{
    public string LlamaServer { get; set; } = "";
    public string ModelsDir { get; set; } = "";
    public string ProfilesDir { get; set; } = Path.Combine(Root, "profiles");
    public string RunsDir { get; set; } = Path.Combine(Root, "runs");
    public string LogsDir { get; set; } = Path.Combine(Root, "logs");
    public string DefaultProfile { get; set; } = "starter";
    public string Editor { get; set; } = Environment.GetEnvironmentVariable("EDITOR") ?? (OperatingSystem.IsWindows() ? "notepad" : "nano");
    public bool ConfirmRestart { get; set; } = true;
    public bool ConfirmRecentFailure { get; set; } = true;
    public int RecentFailureWindowSeconds { get; set; } = 120;
    public int StartupFailureSeconds { get; set; } = 20;
    public string DefaultHost { get; set; } = "0.0.0.0";
    public int DefaultPort { get; set; } = 8080;
    public string LoadMessage { get; private set; } = "";
    public bool IsFirstRun { get; private set; }
    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "lltop");
    public static string ConfigPath => Path.Combine(Root, "config.toml");

    public static AppConfig Load()
    {
        var config = new AppConfig { IsFirstRun = !File.Exists(ConfigPath) };
        try
        {
            if (File.Exists(ConfigPath)) Toml.ReadInto(ConfigPath, config);
            config.ProfilesDir = Expand(config.ProfilesDir);
            config.RunsDir = Expand(config.RunsDir);
            config.LogsDir = Expand(config.LogsDir);
            config.LlamaServer = Expand(config.LlamaServer);
            config.ModelsDir = Expand(config.ModelsDir);
            Directory.CreateDirectory(config.ProfilesDir);
            Directory.CreateDirectory(config.RunsDir);
            Directory.CreateDirectory(config.LogsDir);
            config.LoadMessage = $"Config  {ConfigPath}";
        }
        catch (Exception ex) { config.LoadMessage = $"Config error: {ex.Message}"; }
        return config;
    }

    public static string Expand(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value == "~") value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (value.StartsWith("~/") || value.StartsWith("~\\")) value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    public void Save()
    {
        Directory.CreateDirectory(Root);
        var contents = $"llama_server = {Toml.Quote(LlamaServer)}\nmodels_dir = {Toml.Quote(ModelsDir)}\ndefault_profile = {Toml.Quote(DefaultProfile)}\nprofiles_dir = {Toml.Quote(ProfilesDir)}\nruns_dir = {Toml.Quote(RunsDir)}\nlogs_dir = {Toml.Quote(LogsDir)}\neditor = {Toml.Quote(Editor)}\nconfirm_restart = {ConfirmRestart.ToString().ToLowerInvariant()}\nconfirm_recent_failure = {ConfirmRecentFailure.ToString().ToLowerInvariant()}\nrecent_failure_window_seconds = {RecentFailureWindowSeconds}\nstartup_failure_seconds = {StartupFailureSeconds}\ndefault_host = {Toml.Quote(DefaultHost)}\ndefault_port = {DefaultPort}\n";
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, ConfigPath, true);
        IsFirstRun = false;
    }
}
