package history

import (
	"math"
	"sort"
	"strings"
)

type MetricSummary struct {
	Count   int
	Latest  float64
	Average float64
	Median  float64
	Min     float64
	Max     float64
	Series  []float64
}

type ProfileSummary struct {
	ProfileName     string
	RunCount        int
	PromptSpeed     MetricSummary
	GenerationSpeed MetricSummary
}

func SummarizeProfileRuns(records []*RunRecord, profileName string) ProfileSummary {
	summary := ProfileSummary{ProfileName: profileName}
	promptValues := make([]float64, 0, len(records))
	evalValues := make([]float64, 0, len(records))

	for _, record := range records {
		if record == nil || !strings.EqualFold(record.ProfileName, profileName) {
			continue
		}
		summary.RunCount++
		if record.LastPromptTokensPerSec > 0 {
			promptValues = append(promptValues, record.LastPromptTokensPerSec)
		}
		if record.LastEvalTokensPerSec > 0 {
			evalValues = append(evalValues, record.LastEvalTokensPerSec)
		}
	}

	summary.PromptSpeed = summarizeMetric(promptValues)
	summary.GenerationSpeed = summarizeMetric(evalValues)
	return summary
}

func summarizeMetric(values []float64) MetricSummary {
	if len(values) == 0 {
		return MetricSummary{}
	}

	series := append([]float64(nil), values...)
	sorted := append([]float64(nil), values...)
	sort.Float64s(sorted)

	sum := 0.0
	for _, value := range values {
		sum += value
	}

	return MetricSummary{
		Count:   len(values),
		Latest:  values[len(values)-1],
		Average: sum / float64(len(values)),
		Median:  median(sorted),
		Min:     sorted[0],
		Max:     sorted[len(sorted)-1],
		Series:  series,
	}
}

func median(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}
	mid := len(values) / 2
	if len(values)%2 == 1 {
		return values[mid]
	}
	return (values[mid-1] + values[mid]) / 2
}

func Sparkline(values []float64) string {
	if len(values) == 0 {
		return ""
	}
	levels := []rune("▁▂▃▄▅▆▇█")
	minValue := values[0]
	maxValue := values[0]
	for _, value := range values[1:] {
		minValue = math.Min(minValue, value)
		maxValue = math.Max(maxValue, value)
	}
	if maxValue <= minValue {
		return strings.Repeat(string(levels[len(levels)/2]), len(values))
	}

	var b strings.Builder
	for _, value := range values {
		idx := int(math.Round((value - minValue) / (maxValue - minValue) * float64(len(levels)-1)))
		if idx < 0 {
			idx = 0
		}
		if idx >= len(levels) {
			idx = len(levels) - 1
		}
		b.WriteRune(levels[idx])
	}
	return b.String()
}
