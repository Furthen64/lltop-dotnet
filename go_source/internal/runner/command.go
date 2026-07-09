package runner

import (
	"fmt"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/Furthen64/lltop/internal/config"
)

type CommandSpec struct {
	Path    string
	Args    []string
	Display string
}

func BuildCommand(cfg *config.GlobalConfig, profile *config.Profile) (CommandSpec, error) {
	p := *profile
	p.ApplyDefaults(cfg)

	cmdPath := config.EffectiveLlamaServer(cfg, &p)
	if cmdPath == "" {
		return CommandSpec{}, fmt.Errorf("llama_server path is required")
	}
	if !filepath.IsAbs(cmdPath) {
		expanded, err := config.ExpandPath(cmdPath)
		if err != nil {
			return CommandSpec{}, err
		}
		cmdPath = expanded
	}

	args := []string{
		"-m", p.Model,
		"--port", strconv.Itoa(p.Port),
	}
	if p.Host != "" {
		args = append(args, "--host", p.Host)
	}
	if p.Alias != "" {
		args = append(args, "-a", p.Alias)
	}
	args = append(args,
		"-c", strconv.Itoa(p.Ctx),
		"-ngl", strconv.Itoa(p.NGL),
	)
	if p.CacheK != "" {
		args = append(args, "--cache-type-k", p.CacheK)
	}
	if p.CacheV != "" {
		args = append(args, "--cache-type-v", p.CacheV)
	}
	if p.FlashAttn != "" {
		args = append(args, "--flash-attn", p.FlashAttn)
	}
	args = append(args,
		"--temp", formatFloat(p.Temp),
		"--top-p", formatFloat(p.TopP),
		"--top-k", strconv.Itoa(p.TopK),
		"--min-p", formatFloat(p.MinP),
		"-b", strconv.Itoa(p.Batch),
		"-ub", strconv.Itoa(p.UBatch),
		"--parallel", strconv.Itoa(p.Parallel),
	)
	if p.Threads > 0 {
		args = append(args, "--threads", strconv.Itoa(p.Threads))
	}
	if p.Metrics {
		args = append(args, "--metrics")
	}
	if p.Jinja {
		args = append(args, "--jinja")
	}
	if p.Reasoning != "" {
		args = append(args, "--reasoning", p.Reasoning)
	}
	args = append(args, "--reasoning-budget", strconv.Itoa(p.ReasoningBudget))
	if p.NoMmap {
		args = append(args, "--no-mmap")
	}
	if p.ChatTemplate != "" {
		args = append(args, "--chat-template", p.ChatTemplate)
	}
	args = append(args, filterConflictingExtraArgs(p.ExtraArgs)...)

	var b strings.Builder
	b.WriteString(shellQuote(cmdPath))
	for _, arg := range args {
		b.WriteByte(' ')
		b.WriteString(shellQuote(arg))
	}

	return CommandSpec{Path: cmdPath, Args: args, Display: b.String()}, nil
}

func filterConflictingExtraArgs(args []string) []string {
	filtered := make([]string, 0, len(args))
	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "-fa" || arg == "--flash-attn":
			if i+1 < len(args) && isFlashAttnArgValue(args[i+1]) {
				i++
			}
			continue
		case strings.HasPrefix(arg, "--flash-attn="):
			continue
		case strings.HasPrefix(arg, "-fa="):
			continue
		default:
			filtered = append(filtered, arg)
		}
	}
	return filtered
}

func isFlashAttnArgValue(value string) bool {
	return config.IsValidFlashAttnValue(value)
}

func formatFloat(v float64) string {
	return strconv.FormatFloat(v, 'f', -1, 64)
}

func shellQuote(s string) string {
	if s == "" {
		return "''"
	}
	if !strings.ContainsAny(s, " \t\n\"'`$&|;()<>{}[]*?!") {
		return s
	}
	return "'" + strings.ReplaceAll(s, "'", "'\\''") + "'"
}
