package config

import (
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
)

const wizardModelSearchDepth = 3
const llamaServerSearchDepth = 4

func RunFirstStartWizard(cfg *GlobalConfig) (string, error) {
	if cfg == nil {
		return "", fmt.Errorf("config is required")
	}
	if !isInteractiveTerminal() {
		return "Run again in an interactive terminal to finish first-run setup.", nil
	}

	defaultLlama := firstNonEmpty(cfg.LlamaServer, detectLlamaServerPath())
	llamaPath, modelsDir, err := runFirstRunWizardTUI(defaultLlama, cfg.ModelsDir)
	if err != nil {
		return "", err
	}
	cfg.LlamaServer = llamaPath
	cfg.ModelsDir = modelsDir

	if err := EnsureDirs(cfg); err != nil {
		return "", err
	}

	configPath, err := ConfigPath()
	if err != nil {
		return "", err
	}
	if err := WriteConfig(configPath, cfg); err != nil {
		return "", err
	}
	if err := ensureStarterProfile(cfg); err != nil {
		return "", err
	}

	if modelsDir == "" {
		return "Saved first-run setup. Add models_dir later to auto-generate profiles.", nil
	}

	models, err := DiscoverModelFiles(modelsDir, wizardModelSearchDepth)
	if err != nil {
		return "", err
	}
	if len(models) == 0 {
		return fmt.Sprintf("Saved first-run setup. No models found under %s.", modelsDir), nil
	}

	created, err := GenerateProfilesForModels(cfg, models)
	if err != nil {
		return "", err
	}
	return fmt.Sprintf("Saved first-run setup. Found %d model(s), created %d profile(s).", len(models), created), nil
}

func ensureStarterProfile(cfg *GlobalConfig) error {
	starterPath := filepath.Join(cfg.ProfilesDir, "starter.toml")
	if _, err := os.Stat(starterPath); err == nil {
		return nil
	} else if !os.IsNotExist(err) {
		return err
	}
	starter := DefaultProfile(cfg, "starter")
	starter.Description = "Starter profile"
	return SaveProfile(starterPath, starter)
}

func DiscoverModelFiles(root string, maxDepth int) ([]string, error) {
	if root == "" {
		return nil, nil
	}
	root = filepath.Clean(root)
	info, err := os.Stat(root)
	if err != nil {
		return nil, err
	}
	if !info.IsDir() {
		return nil, fmt.Errorf("models path must be a directory")
	}

	var models []string
	err = filepath.WalkDir(root, func(path string, d fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return nil
		}
		if path != root {
			rel, relErr := filepath.Rel(root, path)
			if relErr != nil {
				return nil
			}
			depth := strings.Count(rel, string(filepath.Separator)) + 1
			if depth > maxDepth {
				if d.IsDir() {
					return filepath.SkipDir
				}
				return nil
			}
		}
		if d.IsDir() {
			return nil
		}
		ext := strings.ToLower(filepath.Ext(d.Name()))
		if ext == ".gguf" || ext == ".bin" {
			models = append(models, path)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	sort.Strings(models)
	return models, nil
}

func GenerateProfilesForModels(cfg *GlobalConfig, models []string) (int, error) {
	if cfg == nil {
		return 0, fmt.Errorf("config is required")
	}

	existing := map[string]struct{}{}
	entries, err := filepath.Glob(filepath.Join(cfg.ProfilesDir, "*.toml"))
	if err != nil {
		return 0, err
	}
	for _, entry := range entries {
		name := strings.TrimSuffix(filepath.Base(entry), filepath.Ext(entry))
		existing[name] = struct{}{}
	}

	created := 0
	for _, modelPath := range models {
		baseName := strings.TrimSuffix(filepath.Base(modelPath), filepath.Ext(modelPath))
		slug := UniqueProfileSlug(SlugifyName(baseName), existing)
		existing[slug] = struct{}{}
		profilePath := filepath.Join(cfg.ProfilesDir, slug+".toml")
		if _, err := os.Stat(profilePath); err == nil {
			continue
		} else if !os.IsNotExist(err) {
			return created, err
		}

		profile := DefaultProfile(cfg, slug)
		profile.Description = "Auto-generated from first-run setup"
		profile.Model = modelPath
		if err := SaveProfile(profilePath, profile); err != nil {
			return created, err
		}
		created++
	}
	return created, nil
}

func UniqueProfileSlug(base string, existing map[string]struct{}) string {
	if _, ok := existing[base]; !ok {
		return base
	}
	for i := 2; ; i++ {
		candidate := fmt.Sprintf("%s-%d", base, i)
		if _, ok := existing[candidate]; !ok {
			return candidate
		}
	}
}

func requireExecutableFile(path string) error {
	if path == "" {
		return fmt.Errorf("path is required")
	}
	info, err := os.Stat(path)
	if err != nil {
		return err
	}
	if info.IsDir() {
		return fmt.Errorf("path must point to a file")
	}
	if !isExecutableFile(path, info.Mode(), runtime.GOOS, os.Getenv("PATHEXT")) {
		return fmt.Errorf("file is not executable")
	}
	return nil
}

func resolveLlamaServerPath(path string) (string, error) {
	if path == "" {
		return "", fmt.Errorf("path is required")
	}
	info, err := os.Stat(path)
	if err != nil {
		return "", err
	}
	if !info.IsDir() {
		if err := requireExecutableFile(path); err != nil {
			return "", err
		}
		return path, nil
	}
	matches, err := findNamedExecutables(path, LlamaServerBinary, llamaServerSearchDepth)
	if err != nil {
		return "", err
	}
	switch len(matches) {
	case 0:
		return "", fmt.Errorf("no executable llama-server found under %s", path)
	case 1:
		return matches[0], nil
	default:
		return "", fmt.Errorf("multiple executable llama-server binaries found, use full path: %s", strings.Join(matches, ", "))
	}
}

func findNamedExecutables(root, filename string, maxDepth int) ([]string, error) {
	root = filepath.Clean(root)
	var matches []string
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return nil
		}
		if path != root {
			rel, relErr := filepath.Rel(root, path)
			if relErr != nil {
				return nil
			}
			depth := strings.Count(rel, string(filepath.Separator)) + 1
			if depth > maxDepth {
				if d.IsDir() {
					return filepath.SkipDir
				}
				return nil
			}
		}
		if d.IsDir() {
			return nil
		}
		if !executableNameMatches(d.Name(), filename, runtime.GOOS, os.Getenv("PATHEXT")) {
			return nil
		}
		info, err := d.Info()
		if err != nil {
			return nil
		}
		if !isExecutableFile(path, info.Mode(), runtime.GOOS, os.Getenv("PATHEXT")) {
			return nil
		}
		matches = append(matches, path)
		return nil
	})
	if err != nil {
		return nil, err
	}
	sort.Strings(matches)
	return matches, nil
}

func optionalDirectory(path string) error {
	if path == "" {
		return nil
	}
	info, err := os.Stat(path)
	if err != nil {
		return err
	}
	if !info.IsDir() {
		return fmt.Errorf("path must point to a directory")
	}
	return nil
}

func detectLlamaServerPath() string {
	path, err := exec.LookPath(LlamaServerBinary)
	if err != nil {
		return ""
	}
	return path
}

func isInteractiveTerminal() bool {
	info, err := os.Stdin.Stat()
	if err != nil {
		return false
	}
	return info.Mode()&os.ModeCharDevice != 0
}

func firstNonEmpty(values ...string) string {
	for _, v := range values {
		if strings.TrimSpace(v) != "" {
			return v
		}
	}
	return ""
}

func executableNameMatches(name, target, goos, pathExt string) bool {
	if goos != "windows" {
		return name == target
	}
	if strings.EqualFold(name, target) {
		return true
	}
	if filepath.Ext(target) != "" {
		return false
	}
	for _, ext := range executableExtensions(goos, pathExt) {
		if strings.EqualFold(name, target+ext) {
			return true
		}
	}
	return false
}

func isExecutableFile(path string, mode fs.FileMode, goos, pathExt string) bool {
	if goos != "windows" {
		return mode&0o111 != 0
	}
	ext := strings.ToLower(filepath.Ext(path))
	if ext == "" {
		return false
	}
	for _, allowed := range executableExtensions(goos, pathExt) {
		if ext == allowed {
			return true
		}
	}
	return false
}

func executableExtensions(goos, pathExt string) []string {
	if goos != "windows" {
		return nil
	}
	if strings.TrimSpace(pathExt) == "" {
		pathExt = ".COM;.EXE;.BAT;.CMD"
	}
	raw := strings.Split(pathExt, ";")
	seen := make(map[string]struct{}, len(raw))
	exts := make([]string, 0, len(raw))
	for _, item := range raw {
		ext := strings.TrimSpace(strings.ToLower(item))
		if ext == "" {
			continue
		}
		if !strings.HasPrefix(ext, ".") {
			ext = "." + ext
		}
		if _, ok := seen[ext]; ok {
			continue
		}
		seen[ext] = struct{}{}
		exts = append(exts, ext)
	}
	return exts
}
