package parser

import (
	_ "embed"
	"encoding/json"
	"regexp"
	"strconv"
	"strings"
)

type ParsedLine struct {
	Raw                 string
	PromptEvalMS        float64
	PromptTokens        int
	PromptMSPerToken    float64
	PromptTokensPerSec  float64
	EvalMS              float64
	EvalTokens          int
	EvalMSPerToken      float64
	EvalTokensPerSec    float64
	TotalMS             float64
	TotalTokens         int
	OffloadedLayers     int
	TotalLayers         int
	CUDA0MiB            float64
	CUDAHostMiB         float64
	CPUMiB              float64
	GPUName             string
	GPUTotalMiB         int
	GPUFreeMiB          int
	GPUSelfMiB          int
	GPUModelMiB         int
	GPUContextMiB       int
	GPUComputeMiB       int
	ProgressTokens      int
	ProgressBatchTokens int
	Progress            float64
	ChatFormat          string
	CtxSlotSize         int
	NKeep               int
	TaskTokens          int
	Cancelled           bool
	IsError             bool
	ErrorKind           string
	ErrorMessage        string
	HintKind            string
	HintMessage         string
}

const (
	ErrorKindCUDAOutOfMemory ErrorKind = "cuda_oom"
	ErrorKindLoadModel       ErrorKind = "load_model"
	ErrorKindBind            ErrorKind = "bind"
	ErrorKindUnknownArg      ErrorKind = "unknown_argument"
	ErrorKindInvalidArg      ErrorKind = "invalid_argument"
	ErrorKindOpenModel       ErrorKind = "open_model"
)

type ErrorKind string

var (
	promptEvalRe = regexp.MustCompile(`prompt eval time =\s+(\d+\.\d+) ms /\s+(\d+) tokens.*?(\d+\.\d+) ms per token.*?(\d+\.\d+) tokens per second`)
	evalRe       = regexp.MustCompile(`eval time =\s+(\d+\.\d+) ms /\s+(\d+) tokens.*?(\d+\.\d+) ms per token.*?(\d+\.\d+) tokens per second`)
	totalRe      = regexp.MustCompile(`total time =\s+(\d+\.\d+) ms /\s+(\d+) tokens`)
	offloadRe    = regexp.MustCompile(`load_tensors: offloaded (\d+)/(\d+) layers to GPU`)
	cuda0Re      = regexp.MustCompile(`CUDA0 model buffer size =\s+(\d+\.\d+) MiB`)
	cudaHostRe   = regexp.MustCompile(`CUDA_Host model buffer size =\s+(\d+\.\d+) MiB`)
	cpuRe        = regexp.MustCompile(`CPU model buffer size =\s+(\d+\.\d+) MiB`)
	memoryRe     = regexp.MustCompile(`llama_memory_breakdown_print.*?\((.+?)\).*?\|\s+(\d+)\s*=\s*(\d+)\s+\+.*?\(\s*(\d+)\s+\+\s+(\d+)\s+\+\s+(\d+)\)`)
	progressRe   = regexp.MustCompile(`prompt processing progress, n_tokens = (\d+), batch\.n_tokens = (\d+), progress = (\d+\.\d+)`)
	chatFormatRe = regexp.MustCompile(`params_from_.*?Chat format: (.+)`)
	newPromptRe  = regexp.MustCompile(`new prompt, n_ctx_slot = (\d+), n_keep = (\d+), task\.n_tokens = (\d+)`)
	hintRules    = mustLoadHintRules()
)

//go:embed hint_rules.json
var hintRulesJSON []byte

type hintRule struct {
	Kind     string   `json:"kind"`
	Message  string   `json:"message"`
	MatchAll []string `json:"match_all"`
}

func ParseLine(line string) ParsedLine {
	p := ParsedLine{Raw: line}

	if m := promptEvalRe.FindStringSubmatch(line); len(m) == 5 {
		p.PromptEvalMS = atof(m[1])
		p.PromptTokens = atoi(m[2])
		p.PromptMSPerToken = atof(m[3])
		p.PromptTokensPerSec = atof(m[4])
	}
	if m := evalRe.FindStringSubmatch(line); len(m) == 5 {
		p.EvalMS = atof(m[1])
		p.EvalTokens = atoi(m[2])
		p.EvalMSPerToken = atof(m[3])
		p.EvalTokensPerSec = atof(m[4])
	}
	if m := totalRe.FindStringSubmatch(line); len(m) == 3 {
		p.TotalMS = atof(m[1])
		p.TotalTokens = atoi(m[2])
	}
	if m := offloadRe.FindStringSubmatch(line); len(m) == 3 {
		p.OffloadedLayers = atoi(m[1])
		p.TotalLayers = atoi(m[2])
	}
	if m := cuda0Re.FindStringSubmatch(line); len(m) == 2 {
		p.CUDA0MiB = atof(m[1])
	}
	if m := cudaHostRe.FindStringSubmatch(line); len(m) == 2 {
		p.CUDAHostMiB = atof(m[1])
	}
	if m := cpuRe.FindStringSubmatch(line); len(m) == 2 {
		p.CPUMiB = atof(m[1])
	}
	if m := memoryRe.FindStringSubmatch(line); len(m) == 7 {
		p.GPUName = strings.TrimSpace(m[1])
		p.GPUTotalMiB = atoi(m[2])
		p.GPUFreeMiB = atoi(m[3])
		p.GPUModelMiB = atoi(m[4])
		p.GPUContextMiB = atoi(m[5])
		p.GPUComputeMiB = atoi(m[6])
		p.GPUSelfMiB = p.GPUModelMiB + p.GPUContextMiB + p.GPUComputeMiB
	}
	if m := progressRe.FindStringSubmatch(line); len(m) == 4 {
		p.ProgressTokens = atoi(m[1])
		p.ProgressBatchTokens = atoi(m[2])
		p.Progress = atof(m[3])
	}
	if m := chatFormatRe.FindStringSubmatch(line); len(m) == 2 {
		p.ChatFormat = strings.TrimSpace(m[1])
	}
	if m := newPromptRe.FindStringSubmatch(line); len(m) == 4 {
		p.CtxSlotSize = atoi(m[1])
		p.NKeep = atoi(m[2])
		p.TaskTokens = atoi(m[3])
	}
	if strings.Contains(line, "stop: cancel task") {
		p.Cancelled = true
	}

	if hintKind, hintMessage, ok := classifyHint(line); ok {
		p.HintKind = hintKind
		p.HintMessage = hintMessage
	}

	lower := strings.ToLower(line)
	switch {
	case strings.Contains(lower, "cuda out of memory"):
		p.IsError = true
		p.ErrorKind = string(ErrorKindCUDAOutOfMemory)
	case strings.Contains(lower, "failed to load model"):
		p.IsError = true
		p.ErrorKind = string(ErrorKindLoadModel)
	case strings.Contains(lower, "failed to bind"):
		p.IsError = true
		p.ErrorKind = string(ErrorKindBind)
	case strings.Contains(lower, "unknown argument"):
		p.IsError = true
		p.ErrorKind = string(ErrorKindUnknownArg)
	case strings.Contains(lower, "invalid argument"):
		p.IsError = true
		p.ErrorKind = string(ErrorKindInvalidArg)
	case strings.Contains(lower, "cannot open model"):
		p.IsError = true
		p.ErrorKind = string(ErrorKindOpenModel)
	}
	if p.IsError {
		p.ErrorMessage = strings.TrimSpace(line)
	}

	return p
}

func classifyHint(line string) (kind string, message string, ok bool) {
	lower := strings.ToLower(line)
	for _, rule := range hintRules {
		if matchesAll(lower, rule.MatchAll) {
			return rule.Kind, rule.Message, true
		}
	}
	return "", "", false
}

func mustLoadHintRules() []hintRule {
	var rules []hintRule
	if err := json.Unmarshal(hintRulesJSON, &rules); err != nil {
		panic("parser: invalid hint_rules.json: " + err.Error())
	}
	for _, rule := range rules {
		if strings.TrimSpace(rule.Kind) == "" || strings.TrimSpace(rule.Message) == "" || len(rule.MatchAll) == 0 {
			panic("parser: invalid hint rule in hint_rules.json")
		}
	}
	return rules
}

func matchesAll(line string, patterns []string) bool {
	for _, pattern := range patterns {
		if !strings.Contains(line, strings.ToLower(pattern)) {
			return false
		}
	}
	return true
}

func atoi(s string) int {
	v, _ := strconv.Atoi(s)
	return v
}

func atof(s string) float64 {
	v, _ := strconv.ParseFloat(s, 64)
	return v
}
