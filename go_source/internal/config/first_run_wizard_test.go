package config

import (
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestDiscoverModelFilesRespectsDepth(t *testing.T) {
	root := t.TempDir()

	mkFile := func(rel string) {
		t.Helper()
		path := filepath.Join(root, rel)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatalf("mkdir failed: %v", err)
		}
		if err := os.WriteFile(path, []byte("x"), 0o644); err != nil {
			t.Fatalf("write failed: %v", err)
		}
	}

	mkFile("top.gguf")
	mkFile("one/two/model.bin")
	mkFile("one/two/three/deep.gguf")
	mkFile("not-a-model.txt")

	models, err := DiscoverModelFiles(root, 3)
	if err != nil {
		t.Fatalf("DiscoverModelFiles failed: %v", err)
	}

	want := map[string]struct{}{
		filepath.Join(root, "top.gguf"):          {},
		filepath.Join(root, "one/two/model.bin"): {},
	}
	if len(models) != len(want) {
		t.Fatalf("expected %d models, got %d: %v", len(want), len(models), models)
	}
	for _, model := range models {
		if _, ok := want[model]; !ok {
			t.Fatalf("unexpected model path: %s", model)
		}
	}
}

func TestGenerateProfilesForModelsCreatesUniqueProfileFiles(t *testing.T) {
	profilesDir := t.TempDir()
	cfg := DefaultGlobalConfig()
	cfg.ProfilesDir = profilesDir
	cfg.LlamaServer = "/usr/bin/llama-server"

	existing := DefaultProfile(cfg, "foo")
	existing.Model = "/models/existing.gguf"
	if err := SaveProfile(filepath.Join(profilesDir, "foo.toml"), existing); err != nil {
		t.Fatalf("failed to seed existing profile: %v", err)
	}

	models := []string{
		"/models/foo.gguf",
		"/models/bar.gguf",
	}
	created, err := GenerateProfilesForModels(cfg, models)
	if err != nil {
		t.Fatalf("GenerateProfilesForModels failed: %v", err)
	}
	if created != 2 {
		t.Fatalf("expected 2 profiles created, got %d", created)
	}

	if _, err := os.Stat(filepath.Join(profilesDir, "foo-2.toml")); err != nil {
		t.Fatalf("expected foo-2.toml: %v", err)
	}
	barPath := filepath.Join(profilesDir, "bar.toml")
	if _, err := os.Stat(barPath); err != nil {
		t.Fatalf("expected bar.toml: %v", err)
	}

	data, err := os.ReadFile(barPath)
	if err != nil {
		t.Fatalf("failed to read generated profile: %v", err)
	}
	if !strings.Contains(string(data), `chat_template = "chatml"`) {
		t.Fatalf("expected generated profile to include default chat template, got %s", data)
	}
	if !strings.Contains(string(data), `flash_attn = "auto"`) {
		t.Fatalf("expected generated profile to include default flash_attn, got %s", data)
	}
}

func TestResolveLlamaServerPathAcceptsExecutableFile(t *testing.T) {
	root := t.TempDir()
	bin := filepath.Join(root, "llama-server")
	if err := os.WriteFile(bin, []byte("#!/bin/sh\n"), 0o755); err != nil {
		t.Fatalf("write failed: %v", err)
	}

	got, err := resolveLlamaServerPath(bin)
	if err != nil {
		t.Fatalf("resolveLlamaServerPath failed: %v", err)
	}
	if got != bin {
		t.Fatalf("expected %s, got %s", bin, got)
	}
}

func TestResolveLlamaServerPathFindsBinaryInDirectory(t *testing.T) {
	root := t.TempDir()
	bin := filepath.Join(root, "build/bin/llama-server")
	if err := os.MkdirAll(filepath.Dir(bin), 0o755); err != nil {
		t.Fatalf("mkdir failed: %v", err)
	}
	if err := os.WriteFile(bin, []byte("#!/bin/sh\n"), 0o755); err != nil {
		t.Fatalf("write failed: %v", err)
	}

	got, err := resolveLlamaServerPath(root)
	if err != nil {
		t.Fatalf("resolveLlamaServerPath failed: %v", err)
	}
	if got != bin {
		t.Fatalf("expected %s, got %s", bin, got)
	}
}

func TestResolveLlamaServerPathRejectsMultipleBinariesInDirectory(t *testing.T) {
	root := t.TempDir()
	binA := filepath.Join(root, "build/bin/llama-server")
	binB := filepath.Join(root, "other/llama-server")
	for _, path := range []string{binA, binB} {
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatalf("mkdir failed: %v", err)
		}
		if err := os.WriteFile(path, []byte("#!/bin/sh\n"), 0o755); err != nil {
			t.Fatalf("write failed: %v", err)
		}
	}

	_, err := resolveLlamaServerPath(root)
	if err == nil {
		t.Fatal("expected error for multiple binaries")
	}
	if !strings.Contains(err.Error(), "multiple executable llama-server binaries found") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestValidateWizardPathInputUsesDefault(t *testing.T) {
	got, err := validateWizardPathInput("", "/tmp/default", func(path string) (string, error) {
		return path, nil
	})
	if err != nil {
		t.Fatalf("validateWizardPathInput failed: %v", err)
	}
	if got != "/tmp/default" {
		t.Fatalf("expected default path, got %s", got)
	}
}

func TestValidateWizardPathInputWrapsValidatorErrors(t *testing.T) {
	_, err := validateWizardPathInput("/tmp/value", "", func(path string) (string, error) {
		return "", os.ErrNotExist
	})
	if err == nil {
		t.Fatal("expected error")
	}
	if !strings.Contains(err.Error(), "invalid path") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestExecutableNameMatchesWindowsExe(t *testing.T) {
	if !executableNameMatches("llama-server.exe", "llama-server", "windows", ".EXE;.BAT") {
		t.Fatal("expected windows executable name to match base name")
	}
}

func TestIsExecutableFileWindowsUsesExtension(t *testing.T) {
	if !isExecutableFile(`C:\llama\llama-server.exe`, fs.FileMode(0o644), "windows", ".EXE;.BAT") {
		t.Fatal("expected .exe file to be executable on windows")
	}
	if isExecutableFile(`C:\llama\llama-server.txt`, fs.FileMode(0o777), "windows", ".EXE;.BAT") {
		t.Fatal("did not expect .txt file to be executable on windows")
	}
}
