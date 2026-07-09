package config

import (
	"fmt"
	"os"
	"runtime"
	"strings"
)

var validFlashAttnValues = map[string]struct{}{
	"auto": {},
	"on":   {},
	"off":  {},
}

var validReasoningValues = map[string]struct{}{
	"auto": {},
	"on":   {},
	"off":  {},
}

func IsValidFlashAttnValue(value string) bool {
	_, ok := validFlashAttnValues[strings.ToLower(value)]
	return ok
}

func IsValidReasoningValue(value string) bool {
	_, ok := validReasoningValues[strings.ToLower(value)]
	return ok
}

func EffectiveLlamaServer(cfg *GlobalConfig, p *Profile) string {
	if p != nil && p.LlamaServer != "" {
		return p.LlamaServer
	}
	if cfg != nil {
		return cfg.LlamaServer
	}
	return ""
}

func ValidateProfileConfig(p *Profile) error {
	if p.Name == "" {
		return fmt.Errorf("profile name is required")
	}
	if p.Port < 1 || p.Port > 65535 {
		return fmt.Errorf("port must be between 1 and 65535")
	}
	if p.FlashAttn != "" && !IsValidFlashAttnValue(p.FlashAttn) {
		return fmt.Errorf("flash_attn must be one of: auto, on, off")
	}
	if p.Reasoning != "" && !IsValidReasoningValue(p.Reasoning) {
		return fmt.Errorf("reasoning must be one of: auto, on, off")
	}
	if p.ReasoningBudget < -1 {
		return fmt.Errorf("reasoning_budget must be -1 or greater")
	}
	return nil
}

func ValidateLaunchProfile(cfg *GlobalConfig, p *Profile) error {
	if err := ValidateProfileConfig(p); err != nil {
		return err
	}

	llamaServer := EffectiveLlamaServer(cfg, p)
	if llamaServer == "" {
		return fmt.Errorf("llama_server path is required")
	}
	info, err := os.Stat(llamaServer)
	if err != nil {
		return fmt.Errorf("llama_server not found: %w", err)
	}
	if !isExecutableFile(llamaServer, info.Mode(), runtime.GOOS, os.Getenv("PATHEXT")) {
		return fmt.Errorf("llama_server is not executable")
	}

	if p.Model == "" {
		return fmt.Errorf("model path is required")
	}
	if _, err := os.Stat(p.Model); err != nil {
		return fmt.Errorf("model not found: %w", err)
	}
	if p.Ctx <= 0 {
		return fmt.Errorf("ctx must be greater than 0")
	}
	if p.NGL < 0 {
		return fmt.Errorf("ngl must be greater than or equal to 0")
	}
	if p.Batch <= 0 {
		return fmt.Errorf("batch must be greater than 0")
	}
	if p.UBatch <= 0 {
		return fmt.Errorf("ubatch must be greater than 0")
	}
	if p.Parallel <= 0 {
		return fmt.Errorf("parallel must be greater than 0")
	}
	return nil
}
