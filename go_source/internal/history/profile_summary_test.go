package history

import "testing"

func TestSummarizeProfileRuns(t *testing.T) {
	records := []*RunRecord{
		{ProfileName: "alpha", LastPromptTokensPerSec: 100, LastEvalTokensPerSec: 4},
		{ProfileName: "alpha", LastPromptTokensPerSec: 120, LastEvalTokensPerSec: 6},
		{ProfileName: "beta", LastPromptTokensPerSec: 999, LastEvalTokensPerSec: 999},
		{ProfileName: "alpha", LastPromptTokensPerSec: 80, LastEvalTokensPerSec: 2},
	}

	summary := SummarizeProfileRuns(records, "alpha")

	if summary.RunCount != 3 {
		t.Fatalf("expected 3 alpha runs, got %d", summary.RunCount)
	}
	if summary.GenerationSpeed.Count != 3 {
		t.Fatalf("expected 3 generation datapoints, got %d", summary.GenerationSpeed.Count)
	}
	if summary.GenerationSpeed.Latest != 2 {
		t.Fatalf("expected latest generation speed 2, got %.2f", summary.GenerationSpeed.Latest)
	}
	if summary.GenerationSpeed.Average != 4 {
		t.Fatalf("expected average generation speed 4, got %.2f", summary.GenerationSpeed.Average)
	}
	if summary.GenerationSpeed.Median != 4 {
		t.Fatalf("expected median generation speed 4, got %.2f", summary.GenerationSpeed.Median)
	}
	if summary.PromptSpeed.Min != 80 || summary.PromptSpeed.Max != 120 {
		t.Fatalf("unexpected prompt range %.2f..%.2f", summary.PromptSpeed.Min, summary.PromptSpeed.Max)
	}
}

func TestSparkline(t *testing.T) {
	got := Sparkline([]float64{1, 2, 3, 4})
	if got == "" {
		t.Fatal("expected sparkline output")
	}
	if len([]rune(got)) != 4 {
		t.Fatalf("expected 4 sparkline glyphs, got %q", got)
	}
}
