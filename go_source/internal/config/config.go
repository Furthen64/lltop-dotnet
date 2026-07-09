package config

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/BurntSushi/toml"
)

const MaxLogLines = 500

const LlamaServerBinary = "llama-server"

type GlobalConfig struct {
	LlamaServer             string `toml:"llama_server"`
	ModelsDir               string `toml:"models_dir"`
	DefaultProfile          string `toml:"default_profile"`
	ProfilesDir             string `toml:"profiles_dir"`
	RunsDir                 string `toml:"runs_dir"`
	LogsDir                 string `toml:"logs_dir"`
	Editor                  string `toml:"editor"`
	ConfirmRestart          bool   `toml:"confirm_restart"`
	ConfirmRecentFailure    bool   `toml:"confirm_recent_failure"`
	RecentFailureWindowSecs int    `toml:"recent_failure_window_seconds"`
	StartupFailureSecs      int    `toml:"startup_failure_seconds"`
	DefaultHost             string `toml:"default_host"`
	DefaultPort             int    `toml:"default_port"`
}

func AppDir() (string, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(home, ".config", "lltop"), nil
}

func ConfigPath() (string, error) {
	root, err := AppDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(root, "config.toml"), nil
}

func DefaultEditor() string {
	if runtime.GOOS == "windows" {
		return "notepad"
	}
	return "nano"
}

func DefaultGlobalConfig() *GlobalConfig {
	root, _ := AppDir()
	editor := os.Getenv("EDITOR")
	if editor == "" {
		editor = DefaultEditor()
	}

	return &GlobalConfig{
		ProfilesDir:             filepath.Join(root, "profiles"),
		RunsDir:                 filepath.Join(root, "runs"),
		LogsDir:                 filepath.Join(root, "logs"),
		Editor:                  editor,
		ConfirmRestart:          true,
		ConfirmRecentFailure:    true,
		RecentFailureWindowSecs: 120,
		StartupFailureSecs:      20,
		DefaultHost:             "0.0.0.0",
		DefaultPort:             8080,
		DefaultProfile:          "starter",
	}
}

func (cfg *GlobalConfig) Normalize() error {
	defaults := DefaultGlobalConfig()

	if cfg.Editor == "" {
		cfg.Editor = defaults.Editor
	}
	if cfg.ProfilesDir == "" {
		cfg.ProfilesDir = defaults.ProfilesDir
	}
	if cfg.RunsDir == "" {
		cfg.RunsDir = defaults.RunsDir
	}
	if cfg.LogsDir == "" {
		cfg.LogsDir = defaults.LogsDir
	}
	if cfg.RecentFailureWindowSecs == 0 {
		cfg.RecentFailureWindowSecs = defaults.RecentFailureWindowSecs
	}
	if cfg.StartupFailureSecs == 0 {
		cfg.StartupFailureSecs = defaults.StartupFailureSecs
	}
	if cfg.DefaultHost == "" {
		cfg.DefaultHost = defaults.DefaultHost
	}
	if cfg.DefaultPort == 0 {
		cfg.DefaultPort = defaults.DefaultPort
	}
	if cfg.DefaultProfile == "" {
		cfg.DefaultProfile = defaults.DefaultProfile
	}

	var err error
	if cfg.LlamaServer != "" {
		cfg.LlamaServer, err = ExpandPath(cfg.LlamaServer)
		if err != nil {
			return err
		}
	}
	if cfg.ModelsDir != "" {
		cfg.ModelsDir, err = ExpandPath(cfg.ModelsDir)
		if err != nil {
			return err
		}
	}
	cfg.ProfilesDir, err = ExpandPath(cfg.ProfilesDir)
	if err != nil {
		return err
	}
	cfg.RunsDir, err = ExpandPath(cfg.RunsDir)
	if err != nil {
		return err
	}
	cfg.LogsDir, err = ExpandPath(cfg.LogsDir)
	if err != nil {
		return err
	}
	return nil
}

func ExpandPath(path string) (string, error) {
	if path == "" {
		return "", nil
	}
	if strings.HasPrefix(path, "~/") || path == "~" {
		home, err := os.UserHomeDir()
		if err != nil {
			return "", err
		}
		if path == "~" {
			return home, nil
		}
		path = filepath.Join(home, strings.TrimPrefix(path, "~/"))
	}
	return filepath.Clean(os.ExpandEnv(path)), nil
}

func EnsureDirs(cfg *GlobalConfig) error {
	root, err := AppDir()
	if err != nil {
		return err
	}
	dirs := []string{root, cfg.ProfilesDir, cfg.RunsDir, cfg.LogsDir}
	for _, dir := range dirs {
		if dir == "" {
			continue
		}
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	return nil
}

func LoadGlobalConfig() (*GlobalConfig, bool, error) {
	cfg := DefaultGlobalConfig()
	if err := cfg.Normalize(); err != nil {
		return nil, false, err
	}

	configPath, err := ConfigPath()
	if err != nil {
		return nil, false, err
	}

	if _, err := os.Stat(configPath); os.IsNotExist(err) {
		return cfg, true, nil
	} else if err != nil {
		return nil, false, err
	}

	if _, err := toml.DecodeFile(configPath, cfg); err != nil {
		return nil, false, err
	}
	if err := cfg.Normalize(); err != nil {
		return nil, false, err
	}
	if err := EnsureDirs(cfg); err != nil {
		return nil, false, err
	}
	return cfg, false, nil
}

func WriteConfig(path string, cfg *GlobalConfig) error {
	var b strings.Builder
	fmt.Fprintf(&b, "llama_server = %q\n", cfg.LlamaServer)
	fmt.Fprintf(&b, "models_dir = %q\n", cfg.ModelsDir)
	fmt.Fprintf(&b, "default_profile = %q\n", cfg.DefaultProfile)
	fmt.Fprintf(&b, "profiles_dir = %q\n", cfg.ProfilesDir)
	fmt.Fprintf(&b, "runs_dir = %q\n", cfg.RunsDir)
	fmt.Fprintf(&b, "logs_dir = %q\n", cfg.LogsDir)
	fmt.Fprintf(&b, "editor = %q\n", cfg.Editor)
	fmt.Fprintf(&b, "confirm_restart = %t\n", cfg.ConfirmRestart)
	fmt.Fprintf(&b, "confirm_recent_failure = %t\n", cfg.ConfirmRecentFailure)
	fmt.Fprintf(&b, "recent_failure_window_seconds = %d\n", cfg.RecentFailureWindowSecs)
	fmt.Fprintf(&b, "startup_failure_seconds = %d\n", cfg.StartupFailureSecs)
	fmt.Fprintf(&b, "default_host = %q\n", cfg.DefaultHost)
	fmt.Fprintf(&b, "default_port = %d\n", cfg.DefaultPort)
	return os.WriteFile(path, []byte(b.String()), 0o644)
}
