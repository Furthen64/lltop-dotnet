package history

import (
	"time"

	"github.com/Furthen64/lltop/internal/config"
)

type RunRecord struct {
	RunID                  string    `json:"run_id"`
	ProfileName            string    `json:"profile_name"`
	BenchmarkLabel         string    `json:"benchmark_label,omitempty"`
	Notes                  string    `json:"notes,omitempty"`
	StartedAt              time.Time `json:"started_at"`
	EndedAt                time.Time `json:"ended_at"`
	DurationSeconds        float64   `json:"duration_seconds"`
	ExitCode               int       `json:"exit_code"`
	ExitReason             string    `json:"exit_reason"`
	LlamaServer            string    `json:"llama_server"`
	Model                  string    `json:"model"`
	Host                   string    `json:"host"`
	Port                   int       `json:"port"`
	Alias                  string    `json:"alias"`
	Ctx                    int       `json:"ctx"`
	NGL                    int       `json:"ngl"`
	CacheK                 string    `json:"cache_k"`
	CacheV                 string    `json:"cache_v"`
	Temp                   float64   `json:"temp"`
	TopP                   float64   `json:"top_p"`
	TopK                   int       `json:"top_k"`
	MinP                   float64   `json:"min_p"`
	Batch                  int       `json:"batch"`
	UBatch                 int       `json:"ubatch"`
	Parallel               int       `json:"parallel"`
	Threads                int       `json:"threads"`
	FlashAttn              string    `json:"flash_attn"`
	Reasoning              string    `json:"reasoning"`
	ReasoningBudget        int       `json:"reasoning_budget"`
	Metrics                bool      `json:"metrics"`
	Jinja                  bool      `json:"jinja"`
	NoMmap                 bool      `json:"no_mmap"`
	ChatTemplate           string    `json:"chat_template"`
	ExtraArgs              []string  `json:"extra_args,omitempty"`
	GeneratedCommand       string    `json:"generated_command"`
	LastPromptTokensPerSec float64   `json:"last_prompt_tokens_per_second"`
	LastEvalTokensPerSec   float64   `json:"last_eval_tokens_per_second"`
	LastGeneratedTokens    int       `json:"last_generated_tokens"`
	LastPromptTokens       int       `json:"last_prompt_tokens"`
	OffloadedLayers        int       `json:"offloaded_layers"`
	TotalLayers            int       `json:"total_layers"`
	GPUTotalMiB            int       `json:"gpu_total_mib"`
	GPUFreeMiB             int       `json:"gpu_free_mib"`
	GPUModelMiB            int       `json:"gpu_model_mib"`
	GPUContextMiB          int       `json:"gpu_context_mib"`
	GPUComputeMiB          int       `json:"gpu_compute_mib"`
	Issues                 []Issue   `json:"issues"`
}

type Issue struct {
	Severity   string  `json:"severity"`
	Kind       string  `json:"kind"`
	Message    string  `json:"message"`
	SeenAtSecs float64 `json:"seen_at_seconds"`
}

type StatsSnapshot struct {
	PromptTokensPerSec float64
	EvalTokensPerSec   float64
	GeneratedTokens    int
	PromptTokens       int
	OffloadedLayers    int
	TotalLayers        int
	GPUTotalMiB        int
	GPUFreeMiB         int
	GPUModelMiB        int
	GPUContextMiB      int
	GPUComputeMiB      int
	Issues             []Issue
}

func NewRunRecord(cfg *config.GlobalConfig, profile *config.Profile, command string, startedAt, endedAt time.Time, exitCode int, exitReason string, stats StatsSnapshot) *RunRecord {
	p := *profile
	p.ApplyDefaults(cfg)
	duration := endedAt.Sub(startedAt).Seconds()
	if duration < 0 {
		duration = 0
	}

	return &RunRecord{
		RunID:                  startedAt.Format("20060102_150405") + "_" + config.SlugifyName(p.Name),
		ProfileName:            p.Name,
		StartedAt:              startedAt,
		EndedAt:                endedAt,
		DurationSeconds:        duration,
		ExitCode:               exitCode,
		ExitReason:             exitReason,
		LlamaServer:            config.EffectiveLlamaServer(cfg, &p),
		Model:                  p.Model,
		Host:                   p.Host,
		Port:                   p.Port,
		Alias:                  p.Alias,
		Ctx:                    p.Ctx,
		NGL:                    p.NGL,
		CacheK:                 p.CacheK,
		CacheV:                 p.CacheV,
		Temp:                   p.Temp,
		TopP:                   p.TopP,
		TopK:                   p.TopK,
		MinP:                   p.MinP,
		Batch:                  p.Batch,
		UBatch:                 p.UBatch,
		Parallel:               p.Parallel,
		Threads:                p.Threads,
		FlashAttn:              p.FlashAttn,
		Reasoning:              p.Reasoning,
		ReasoningBudget:        p.ReasoningBudget,
		Metrics:                p.Metrics,
		Jinja:                  p.Jinja,
		NoMmap:                 p.NoMmap,
		ChatTemplate:           p.ChatTemplate,
		ExtraArgs:              append([]string(nil), p.ExtraArgs...),
		GeneratedCommand:       command,
		LastPromptTokensPerSec: stats.PromptTokensPerSec,
		LastEvalTokensPerSec:   stats.EvalTokensPerSec,
		LastGeneratedTokens:    stats.GeneratedTokens,
		LastPromptTokens:       stats.PromptTokens,
		OffloadedLayers:        stats.OffloadedLayers,
		TotalLayers:            stats.TotalLayers,
		GPUTotalMiB:            stats.GPUTotalMiB,
		GPUFreeMiB:             stats.GPUFreeMiB,
		GPUModelMiB:            stats.GPUModelMiB,
		GPUContextMiB:          stats.GPUContextMiB,
		GPUComputeMiB:          stats.GPUComputeMiB,
		Issues:                 append([]Issue(nil), stats.Issues...),
	}
}
