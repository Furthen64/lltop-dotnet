sealed class AppConfig
{
    public string LlamaServer { get; set; } = "";
    public string ModelsDir { get; set; } = "";
    public string ProfilesDir { get; set; } = Path.Combine(Root, "profiles");
    public string LogsDir { get; set; } = Path.Combine(Root, "logs");
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
            config.LogsDir = Expand(config.LogsDir);
            config.LlamaServer = Expand(config.LlamaServer);
            config.ModelsDir = Expand(config.ModelsDir);
            Directory.CreateDirectory(config.ProfilesDir);
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
        var contents = $"llama_server = {Toml.Quote(LlamaServer)}\nmodels_dir = {Toml.Quote(ModelsDir)}\nprofiles_dir = {Toml.Quote(ProfilesDir)}\nlogs_dir = {Toml.Quote(LogsDir)}\ndefault_host = {Toml.Quote(DefaultHost)}\ndefault_port = {DefaultPort}\n";
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, ConfigPath, true);
        IsFirstRun = false;
    }
}
