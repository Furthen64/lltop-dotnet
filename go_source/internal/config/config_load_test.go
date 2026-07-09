package config

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestLoadGlobalConfigMissingDoesNotCreateFilesBeforeWizard(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)

	cfg, created, err := LoadGlobalConfig()
	if err != nil {
		t.Fatalf("LoadGlobalConfig failed: %v", err)
	}
	if !created {
		t.Fatal("expected created=true when config is missing")
	}
	if cfg == nil {
		t.Fatal("expected config defaults")
	}

	appDir := filepath.Join(home, ".config", "lltop")
	if _, err := os.Stat(appDir); !os.IsNotExist(err) {
		t.Fatalf("expected no app dir before wizard completes, got err=%v", err)
	}
}

func TestLoadProfileDefaultsFlashAttnToAutoWhenMissing(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "legacy.toml")
	content := strings.Join([]string{
		`name = "legacy"`,
		`model = "/models/qwen.gguf"`,
		`host = "0.0.0.0"`,
		`port = 8080`,
		`ctx = 65536`,
		`ngl = 99`,
		`cache_k = "q4_0"`,
		`cache_v = "q8_0"`,
		`temp = 0.100`,
		`top_p = 0.950`,
		`top_k = 40`,
		`min_p = 0.050`,
		`batch = 512`,
		`ubatch = 256`,
		`parallel = 1`,
		`threads = 0`,
		`jinja = true`,
		`metrics = true`,
		`no_mmap = true`,
		`chat_template = "chatml"`,
		`reasoning = "auto"`,
		`reasoning_budget = -1`,
		`extra_args = []`,
	}, "\n") + "\n"
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("failed to write legacy profile: %v", err)
	}

	profile, err := LoadProfile(path)
	if err != nil {
		t.Fatalf("LoadProfile failed: %v", err)
	}
	if profile.FlashAttn != "auto" {
		t.Fatalf("expected missing flash_attn to default to auto, got %q", profile.FlashAttn)
	}
	if profile.Reasoning != "auto" {
		t.Fatalf("expected reasoning to remain auto, got %q", profile.Reasoning)
	}
	if profile.ReasoningBudget != -1 {
		t.Fatalf("expected reasoning_budget to remain -1, got %d", profile.ReasoningBudget)
	}
}

func TestLoadProfilePreservesExplicitZeroMinPAndReasoningBudget(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "zeroes.toml")
	content := strings.Join([]string{
		`name = "zeroes"`,
		`model = "/models/qwen.gguf"`,
		`host = "0.0.0.0"`,
		`port = 8080`,
		`min_p = 0.000`,
		`reasoning_budget = 0`,
	}, "\n") + "\n"
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("failed to write zeroes profile: %v", err)
	}

	profile, err := LoadProfile(path)
	if err != nil {
		t.Fatalf("LoadProfile failed: %v", err)
	}
	profile.ApplyDefaults(nil)
	if profile.MinP != 0 {
		t.Fatalf("expected explicit min_p=0.000 to survive defaults, got %v", profile.MinP)
	}
	if profile.ReasoningBudget != 0 {
		t.Fatalf("expected explicit reasoning_budget=0 to survive defaults, got %d", profile.ReasoningBudget)
	}
}

func TestLoadProfilePreservesExplicitEmptyChatTemplate(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "empty-chat-template.toml")
	content := strings.Join([]string{
		`name = "empty-chat-template"`,
		`model = "/models/qwen.gguf"`,
		`host = "0.0.0.0"`,
		`port = 8080`,
		`chat_template = ""`,
	}, "\n") + "\n"
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("failed to write profile: %v", err)
	}

	profile, err := LoadProfile(path)
	if err != nil {
		t.Fatalf("LoadProfile failed: %v", err)
	}
	profile.ApplyDefaults(nil)
	if profile.ChatTemplate != "" {
		t.Fatalf("expected explicit empty chat_template to survive defaults, got %q", profile.ChatTemplate)
	}
}

func TestLoadProfilePreservesExplicitEmptyOptionalStrings(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "empty-strings.toml")
	content := strings.Join([]string{
		`name = "empty-strings"`,
		`model = "/models/qwen.gguf"`,
		`host = ""`,
		`port = 8080`,
		`cache_k = ""`,
		`cache_v = ""`,
		`flash_attn = ""`,
		`reasoning = ""`,
	}, "\n") + "\n"
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("failed to write profile: %v", err)
	}

	profile, err := LoadProfile(path)
	if err != nil {
		t.Fatalf("LoadProfile failed: %v", err)
	}
	profile.ApplyDefaults(nil)
	if profile.Host != "" {
		t.Fatalf("expected explicit empty host to survive defaults, got %q", profile.Host)
	}
	if profile.CacheK != "" {
		t.Fatalf("expected explicit empty cache_k to survive defaults, got %q", profile.CacheK)
	}
	if profile.CacheV != "" {
		t.Fatalf("expected explicit empty cache_v to survive defaults, got %q", profile.CacheV)
	}
	if profile.FlashAttn != "" {
		t.Fatalf("expected explicit empty flash_attn to survive defaults, got %q", profile.FlashAttn)
	}
	if profile.Reasoning != "" {
		t.Fatalf("expected explicit empty reasoning to survive defaults, got %q", profile.Reasoning)
	}
}

func TestLoadProfileRejectsInvalidFlashAttnValue(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "invalid.toml")
	content := strings.Join([]string{
		`name = "invalid"`,
		`model = "/models/qwen.gguf"`,
		`host = "0.0.0.0"`,
		`port = 8080`,
		`flash_attn = "maybe"`,
	}, "\n") + "\n"
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("failed to write invalid profile: %v", err)
	}

	_, err := LoadProfile(path)
	if err == nil {
		t.Fatal("expected invalid flash_attn to fail validation")
	}
	if !strings.Contains(err.Error(), "flash_attn must be one of: auto, on, off") {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestLoadProfileRejectsInvalidReasoningValue(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "invalid-reasoning.toml")
	content := strings.Join([]string{
		`name = "invalid-reasoning"`,
		`model = "/models/qwen.gguf"`,
		`host = "0.0.0.0"`,
		`port = 8080`,
		`reasoning = "sometimes"`,
	}, "\n") + "\n"
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("failed to write invalid profile: %v", err)
	}

	_, err := LoadProfile(path)
	if err == nil {
		t.Fatal("expected invalid reasoning to fail validation")
	}
	if !strings.Contains(err.Error(), "reasoning must be one of: auto, on, off") {
		t.Fatalf("unexpected error: %v", err)
	}
}
