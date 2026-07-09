package runner

import (
	"strings"
	"testing"

	"github.com/Furthen64/lltop/internal/config"
)

func TestBuildCommandIncludesChatTemplateAfterJinja(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "chatml")
	profile.Model = "/models/qwen.gguf"
	profile.ChatTemplate = "chatml"

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	want := "--jinja --reasoning auto --reasoning-budget -1 --no-mmap --chat-template chatml"
	if !strings.Contains(got, want) {
		t.Fatalf("expected args to contain %q in order, got %q", want, got)
	}
	if !strings.Contains(spec.Display, "--chat-template chatml") {
		t.Fatalf("expected display command to include chat template, got %q", spec.Display)
	}
}

func TestBuildCommandIncludesNoMmapFlag(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "no-mmap")
	profile.Model = "/models/qwen.gguf"

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	if !strings.Contains(got, "--no-mmap") {
		t.Fatalf("expected args to contain --no-mmap, got %q", got)
	}
	if !strings.Contains(spec.Display, "--no-mmap") {
		t.Fatalf("expected display command to include --no-mmap, got %q", spec.Display)
	}
}

func TestBuildCommandIncludesFlashAttnAfterCacheFlags(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "flash-attn")
	profile.Model = "/models/qwen.gguf"
	profile.CacheK = "q4_0"
	profile.CacheV = "q8_0"
	profile.FlashAttn = "on"

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	want := "--cache-type-k q4_0 --cache-type-v q8_0 --flash-attn on"
	if !strings.Contains(got, want) {
		t.Fatalf("expected args to contain %q, got %q", want, got)
	}
}

func TestBuildCommandDefaultsFlashAttnToAuto(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := &config.Profile{
		Name:  "default-flash-attn",
		Model: "/models/qwen.gguf",
	}

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	if !strings.Contains(got, "--flash-attn auto") {
		t.Fatalf("expected args to contain default flash attention setting, got %q", got)
	}
}

func TestBuildCommandPrefersProfileFlashAttnOverExtraArgs(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "dedupe-flash-attn")
	profile.Model = "/models/qwen.gguf"
	profile.FlashAttn = "on"
	profile.ExtraArgs = []string{"--flash-attn", "off", "-fa=auto", "--prio", "2"}

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	if strings.Count(got, "--flash-attn") != 1 {
		t.Fatalf("expected exactly one flash attention flag, got %q", got)
	}
	if !strings.Contains(got, "--flash-attn on") {
		t.Fatalf("expected profile flash attention value to win, got %q", got)
	}
	if !strings.Contains(got, "--prio 2") {
		t.Fatalf("expected unrelated extra args to remain, got %q", got)
	}
}

func TestBuildCommandIncludesReasoningFlags(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "reasoning")
	profile.Model = "/models/qwen.gguf"
	profile.Reasoning = "on"
	profile.ReasoningBudget = 512

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	if !strings.Contains(got, "--reasoning on --reasoning-budget 512") {
		t.Fatalf("expected reasoning flags in args, got %q", got)
	}
}

func TestBuildCommandOmitsChatTemplateWhenExplicitlyEmpty(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "no-chat-template")
	profile.Model = "/models/qwen.gguf"
	profile.ChatTemplate = ""

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	if strings.Contains(got, "--chat-template") {
		t.Fatalf("expected chat template flag to be omitted, got %q", got)
	}
}

func TestBuildCommandOmitsEmptyOptionalStringFlags(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "omit-empty-strings")
	profile.Model = "/models/qwen.gguf"
	profile.Host = ""
	profile.CacheK = ""
	profile.CacheV = ""
	profile.FlashAttn = ""
	profile.Reasoning = ""

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	for _, forbidden := range []string{
		"--host",
		"--cache-type-k",
		"--cache-type-v",
		"--flash-attn",
		"--reasoning ",
	} {
		if strings.Contains(got, forbidden) {
			t.Fatalf("expected %q to be omitted, got %q", forbidden, got)
		}
	}
	if !strings.Contains(got, "--reasoning-budget -1") {
		t.Fatalf("expected reasoning budget to remain explicit, got %q", got)
	}
}

func TestBuildCommandPreservesExplicitZeroMinPAndReasoningBudget(t *testing.T) {
	cfg := config.DefaultGlobalConfig()
	cfg.LlamaServer = "/usr/bin/llama-server"
	profile := config.DefaultProfile(cfg, "zeroes")
	profile.Model = "/models/qwen.gguf"
	profile.MinP = 0
	profile.ReasoningBudget = 0

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		t.Fatalf("BuildCommand failed: %v", err)
	}

	got := strings.Join(spec.Args, " ")
	if !strings.Contains(got, "--min-p 0") {
		t.Fatalf("expected explicit min_p=0 to remain in args, got %q", got)
	}
	if !strings.Contains(got, "--reasoning-budget 0") {
		t.Fatalf("expected explicit reasoning_budget=0 to remain in args, got %q", got)
	}
}
