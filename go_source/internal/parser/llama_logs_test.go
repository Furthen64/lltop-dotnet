package parser

import "testing"

func TestHintRulesLoaded(t *testing.T) {
	if len(hintRules) == 0 {
		t.Fatal("expected embedded hint rules to be loaded")
	}
}

func TestParsePromptEval(t *testing.T) {
	line := "prompt eval time =     810.49 ms /   114 tokens (    7.11 ms per token,   140.66 tokens per second)"
	p := ParseLine(line)
	if p.PromptEvalMS != 810.49 {
		t.Fatalf("PromptEvalMS = %v", p.PromptEvalMS)
	}
	if p.PromptTokens != 114 {
		t.Fatalf("PromptTokens = %v", p.PromptTokens)
	}
	if p.PromptMSPerToken != 7.11 {
		t.Fatalf("PromptMSPerToken = %v", p.PromptMSPerToken)
	}
	if p.PromptTokensPerSec != 140.66 {
		t.Fatalf("PromptTokensPerSec = %v", p.PromptTokensPerSec)
	}
}

func TestParseEvalTime(t *testing.T) {
	line := "eval time =  119350.76 ms /   449 tokens (  265.81 ms per token,     3.76 tokens per second)"
	p := ParseLine(line)
	if p.EvalMS != 119350.76 {
		t.Fatalf("EvalMS = %v", p.EvalMS)
	}
	if p.EvalTokens != 449 {
		t.Fatalf("EvalTokens = %v", p.EvalTokens)
	}
	if p.EvalMSPerToken != 265.81 {
		t.Fatalf("EvalMSPerToken = %v", p.EvalMSPerToken)
	}
	if p.EvalTokensPerSec != 3.76 {
		t.Fatalf("EvalTokensPerSec = %v", p.EvalTokensPerSec)
	}
}

func TestParseOffloadedLayers(t *testing.T) {
	line := "load_tensors: offloaded 26/49 layers to GPU"
	p := ParseLine(line)
	if p.OffloadedLayers != 26 {
		t.Fatalf("OffloadedLayers = %v", p.OffloadedLayers)
	}
	if p.TotalLayers != 49 {
		t.Fatalf("TotalLayers = %v", p.TotalLayers)
	}
}

func TestParsePromptProgress(t *testing.T) {
	line := "prompt processing progress, n_tokens = 9863, batch.n_tokens = 27, progress = 1.000000"
	p := ParseLine(line)
	if p.ProgressTokens != 9863 {
		t.Fatalf("ProgressTokens = %v", p.ProgressTokens)
	}
	if p.ProgressBatchTokens != 27 {
		t.Fatalf("ProgressBatchTokens = %v", p.ProgressBatchTokens)
	}
	if p.Progress != 1.0 {
		t.Fatalf("Progress = %v", p.Progress)
	}
}

func TestParseTotalTime(t *testing.T) {
	line := "total time =  120161.25 ms /   563 tokens"
	p := ParseLine(line)
	if p.TotalMS != 120161.25 {
		t.Fatalf("TotalMS = %v", p.TotalMS)
	}
	if p.TotalTokens != 563 {
		t.Fatalf("TotalTokens = %v", p.TotalTokens)
	}
}

func TestParseKnownHintForGPUAutoFitWarning(t *testing.T) {
	line := "W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 99, abort."
	p := ParseLine(line)
	if p.HintKind != "gpu_layers_autofit_skipped" {
		t.Fatalf("HintKind = %q", p.HintKind)
	}
	if p.HintMessage == "" {
		t.Fatal("expected HintMessage to be populated")
	}
	if p.IsError {
		t.Fatal("expected known hint not to be treated as a parser error")
	}
}
