package ui

import (
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/Furthen64/lltop/internal/history"
	"github.com/Furthen64/lltop/internal/parser"
	"github.com/Furthen64/lltop/internal/runner"
	"github.com/charmbracelet/lipgloss"
)

type statusField struct {
	label string
	value string
}

type mainLayoutSpec struct {
	stacked   bool
	topH      int
	statusH   int
	keysH     int
	leftW     int
	rightW    int
	profilesH int
	logsH     int
}

func (m *Model) View() string {
	width := m.width
	if width <= 0 {
		width = 120
	}
	height := m.height
	if height <= 0 {
		height = 40
	}
	if m.viewMode == notesView {
		return m.renderNotesView(width, height)
	}

	statusBar := m.renderStatusBar(width)
	layout := computeMainLayout(width, height-1, m.showHelp)

	profilesPanel := panelStyle.Width(max(1, layout.leftW-2)).Height(max(1, layout.profilesH-2)).Render(m.renderProfiles())
	logsPanel := panelStyle.Width(max(1, layout.rightW-2)).Height(max(1, layout.logsH-2)).Render(m.renderLogs())
	top := lipgloss.JoinHorizontal(lipgloss.Top, profilesPanel, logsPanel)
	if layout.stacked {
		top = lipgloss.JoinVertical(lipgloss.Left, profilesPanel, logsPanel)
	}

	status := panelStyle.Width(max(1, width-2)).Height(max(1, layout.statusH-2)).Render(m.renderStatus())
	bottomContent := m.renderKeys()
	if m.confirmMode {
		bottomContent = titleStyle.Render("confirm") + "\n\n" + m.confirmPrompt
	}
	bottom := panelStyle.Width(max(1, width-2)).Height(max(1, layout.keysH-2)).Render(bottomContent)

	return lipgloss.JoinVertical(lipgloss.Left, statusBar, top, status, bottom)
}

func (m *Model) renderNotesView(width, height int) string {
	statusBar := m.renderStatusBar(width)
	topH, statusH, keysH := layoutHeights(height-1, m.showHelp)
	leftW, rightW := splitColumns(width, 0.35, 28, 40)

	runsPanel := panelStyle.Width(max(1, leftW-2)).Height(max(1, topH-2)).Render(m.renderNoteRuns())
	notePanel := panelStyle.Width(max(1, rightW-2)).Height(max(1, topH-2)).Render(m.renderNoteContent())
	top := lipgloss.JoinHorizontal(lipgloss.Top, runsPanel, notePanel)

	status := panelStyle.Width(max(1, width-2)).Height(max(1, statusH-2)).Render(m.renderNoteStatus())
	bottomContent := m.renderKeys()
	if m.confirmMode {
		bottomContent = titleStyle.Render("confirm") + "\n\n" + m.confirmPrompt
	}
	bottom := panelStyle.Width(max(1, width-2)).Height(max(1, keysH-2)).Render(bottomContent)

	return lipgloss.JoinVertical(lipgloss.Left, statusBar, top, status, bottom)
}

func (m *Model) renderProfiles() string {
	var b strings.Builder
	b.WriteString(titleStyle.Render("profiles"))
	b.WriteString("\n\n")
	if len(m.profiles) == 0 {
		b.WriteString(dimStyle.Render("No profiles found."))
		return b.String()
	}
	maxNameWidth := 0
	for _, profile := range m.profiles {
		if len(profile.Name) > maxNameWidth {
			maxNameWidth = len(profile.Name)
		}
	}
	for i, profile := range m.profiles {
		icon, iconStyle := m.profileRunStatusIcon(profile.Name)
		name := fmt.Sprintf("%-*s", maxNameWidth, profile.Name)
		size := modelFileSizeText(profile.Model)
		if i == m.selectedIdx {
			line := icon + " " + name
			if size != "" {
				line += "  " + size
			}
			if profile.Description != "" {
				line += "  " + profile.Description
			}
			b.WriteString(selectedStyle.Render(line))
		} else {
			line := iconStyle.Render(icon) + " " + name
			if size != "" {
				line += dimStyle.Render("  " + size)
			}
			if profile.Description != "" {
				line += dimStyle.Render("  " + profile.Description)
			}
			b.WriteString(line)
		}
		if i < len(m.profiles)-1 {
			b.WriteByte('\n')
		}
	}
	return b.String()
}

func (m *Model) profileRunStatusIcon(profileName string) (string, lipgloss.Style) {
	if m.runner != nil && m.runner.Profile != nil && strings.EqualFold(m.runner.Profile.Name, profileName) {
		switch m.runner.Status {
		case runner.StatusRunning:
			return "🔵", runStateRunStyle
		case runner.StatusStopping:
			return "🟡", runStateStopStyle
		}
	}
	if m.profileRunState[strings.ToLower(profileName)] {
		return "⚫", runStateDoneStyle
	}
	return "🟠", lipgloss.NewStyle()
}

func modelFileSizeText(path string) string {
	if path == "" {
		return ""
	}
	info, err := os.Stat(path)
	if err != nil || info.IsDir() {
		return ""
	}
	return formatFileSize(info.Size())
}

func formatFileSize(size int64) string {
	if size < 1024 {
		return fmt.Sprintf("%d B", size)
	}
	units := []string{"KiB", "MiB", "GiB", "TiB"}
	value := float64(size)
	for _, unit := range units {
		value /= 1024
		if value < 1024 {
			return fmt.Sprintf("%.1f %s", value, unit)
		}
	}
	return fmt.Sprintf("%.1f PiB", value/1024)
}

func (m *Model) renderLogs() string {
	header := titleStyle.Render("live log") + " " + dimStyle.Render(fmt.Sprintf("autoscroll=%t", m.logAutoScroll))
	if m.logViewport.Width <= 0 {
		return header + "\n\n"
	}
	return header + "\n\n" + m.logViewport.View()
}

func (m *Model) renderNoteRuns() string {
	var b strings.Builder
	b.WriteString(titleStyle.Render("notes"))
	b.WriteString("\n\n")
	if len(m.noteEntries) == 0 {
		b.WriteString(dimStyle.Render("No runs recorded for this profile."))
		return b.String()
	}
	for i, entry := range m.noteEntries {
		record := entry.Record
		if record == nil {
			continue
		}
		stamp := record.StartedAt.Local().Format("2006-01-02 15:04")
		noteMark := " "
		if strings.TrimSpace(record.Notes) != "" {
			noteMark = "*"
		}
		line := fmt.Sprintf("%s %s  exit:%d  gen:%.2f", noteMark, stamp, record.ExitCode, record.LastEvalTokensPerSec)
		if i == m.noteSelectedIdx {
			b.WriteString(selectedStyle.Render(line))
		} else {
			b.WriteString(line)
		}
		if i < len(m.noteEntries)-1 {
			b.WriteByte('\n')
		}
	}
	return b.String()
}

func (m *Model) renderNoteContent() string {
	header := titleStyle.Render("selected note")
	if ref := m.selectedNoteRef(); ref != nil && ref.Record != nil {
		header += " " + dimStyle.Render(ref.Record.RunID)
	}
	return header + "\n\n" + m.noteViewport.View()
}

func (m *Model) renderStatus() string {
	var b strings.Builder
	b.WriteString(titleStyle.Render("current server"))
	b.WriteString("\n\n")

	fields := make([]statusField, 0, 12)
	profile := m.selectedProfile()
	if m.runner != nil && m.runner.Profile != nil && m.runner.IsRunning() {
		profile = m.runner.Profile
	}
	if profile != nil {
		fields = append(fields,
			statusField{label: "Profile", value: profile.Name},
			statusField{label: "Model", value: profile.Model},
			statusField{label: "Bind", value: fmt.Sprintf("%s:%d", profile.Host, profile.Port)},
			statusField{label: "FlashAttn", value: profile.FlashAttn},
			statusField{label: "Reasoning", value: fmt.Sprintf("%s (%d)", profile.Reasoning, profile.ReasoningBudget)},
		)
	}
	status := runner.StatusStopped
	pid := 0
	externalCmd := ""
	runnerActive := false
	if m.runner != nil {
		status = m.runner.Status
		pid = m.runner.PID
		runnerActive = m.runner.IsRunning()
	}
	if !runnerActive {
		if m.externalProc.PID > 0 {
			status = "externally running"
			pid = m.externalProc.PID
			externalCmd = m.externalProc.Command
		} else if proc, err := detectExternalLlamaServer(os.Getpid()); err == nil && proc.PID > 0 {
			status = "externally running"
			pid = proc.PID
			externalCmd = proc.Command
		}
	}
	statusValue := status
	if pid > 0 {
		statusValue += fmt.Sprintf(" (pid %d)", pid)
	}
	if m.runner != nil && !m.runner.StartTime.IsZero() {
		statusValue += fmt.Sprintf("  uptime %s", time.Since(m.runner.StartTime).Truncate(time.Second))
	}
	fields = append(fields, statusField{label: "Status", value: statusValue})
	launchText := m.renderedLaunchText(externalCmd)
	if launchText != "" {
		fields = append(fields, statusField{label: "Launch", value: launchText})
	}
	fields = append(fields, statusField{
		label: "Throughput",
		value: fmt.Sprintf(
			"prompt %.2f tok/s  eval %.2f tok/s",
			m.stats.PromptTokensPerSec,
			m.stats.EvalTokensPerSec,
		),
	})
	fields = append(fields, statusField{
		label: "Runtime",
		value: fmt.Sprintf(
			"offload %d/%d  progress %.2f",
			m.stats.OffloadedLayers,
			m.stats.TotalLayers,
			m.stats.Progress,
		),
	})
	if m.stats.ChatFormat != "" {
		fields = append(fields, statusField{
			label: "Context",
			value: fmt.Sprintf("chat format %s  ctx slot %d", m.stats.ChatFormat, m.stats.CtxSlotSize),
		})
	}
	b.WriteString(renderStatusFields(fields))
	b.WriteString(renderHistorySummary(m.historySummary))
	if m.stats.LastError != "" {
		b.WriteString(errStyle.Render("last error: " + m.stats.LastError))
		b.WriteByte('\n')
	}
	if m.stats.LastHint != "" {
		b.WriteString(warnStyle.Render("note: " + m.stats.LastHint))
		b.WriteByte('\n')
	}
	if m.statusMsg != "" {
		b.WriteString(infoStyle.Render(m.statusMsg))
	}
	return b.String()
}

func (m *Model) renderNoteStatus() string {
	var b strings.Builder
	b.WriteString(titleStyle.Render("note details"))
	b.WriteString("\n\n")

	profile := m.selectedProfile()
	if profile != nil {
		b.WriteString(fmt.Sprintf("profile: %s\n", profile.Name))
	}
	b.WriteString(fmt.Sprintf("runs: %d\n", len(m.noteEntries)))

	ref := m.selectedNoteRef()
	if ref == nil || ref.Record == nil {
		if m.statusMsg != "" {
			b.WriteString("\n")
			b.WriteString(infoStyle.Render(m.statusMsg))
		}
		return b.String()
	}
	record := ref.Record
	b.WriteString(fmt.Sprintf("run_id: %s\n", record.RunID))
	b.WriteString(fmt.Sprintf("started: %s\n", record.StartedAt.Local().Format(time.RFC3339)))
	b.WriteString(fmt.Sprintf("duration: %.2fs  exit: %d\n", record.DurationSeconds, record.ExitCode))
	b.WriteString(fmt.Sprintf("prompt tok/s: %.2f  eval tok/s: %.2f\n", record.LastPromptTokensPerSec, record.LastEvalTokensPerSec))
	if strings.TrimSpace(record.Notes) == "" {
		b.WriteString(dimStyle.Render("note: empty"))
	} else {
		b.WriteString(okStyle.Render("note: saved"))
	}
	if m.statusMsg != "" {
		b.WriteString("\n")
		b.WriteString(infoStyle.Render(m.statusMsg))
	}
	return b.String()
}

func (m *Model) renderedLaunchText(externalCmd string) string {
	if m.runner != nil && m.runner.IsRunning() && m.currentCommand != "" {
		return m.currentCommand
	}
	if externalCmd != "" {
		return externalCmd
	}
	profile := m.selectedProfile()
	if profile == nil {
		return ""
	}
	spec, err := runner.BuildCommand(m.cfg, profile)
	if err != nil {
		return ""
	}
	return spec.Display
}

func (m *Model) renderStatusBar(width int) string {
	runnerActive := m.runner != nil && m.runner.IsRunning()

	var statusTag, statusInfo string
	var tagStyle lipgloss.Style

	if runnerActive {
		tagStyle = runningStyle
		statusTag = " RUNNING "
		statusInfo = fmt.Sprintf("profile: %s  pid: %d", m.runner.Profile.Name, m.runner.PID)
		if !m.runner.StartTime.IsZero() {
			statusInfo += fmt.Sprintf("  uptime: %s", time.Since(m.runner.StartTime).Truncate(time.Second))
		}
	} else {
		extPID := m.externalProc.PID
		if extPID == 0 {
			if proc, err := detectExternalLlamaServer(os.Getpid()); err == nil && proc.PID > 0 {
				extPID = proc.PID
			}
		}
		if extPID > 0 {
			tagStyle = externalStyle
			statusTag = " EXTERNAL "
			statusInfo = fmt.Sprintf("pid: %d", extPID)
		} else {
			tagStyle = idleStyle
			statusTag = "  IDLE  "
		}
	}

	tag := tagStyle.Render(statusTag)
	padding := width - lipgloss.Width(tag) - lipgloss.Width(statusInfo) - 4
	if padding < 1 {
		padding = 1
	}
	fill := strings.Repeat(" ", padding)
	return statusBarStyle.Render(tag + fill + statusInfo)
}

func (m *Model) renderKeys() string {
	title := titleStyle.Render("keys")
	if m.viewMode == notesView {
		if !m.showHelp {
			return title + "\n\nUp/Down select run  PgUp/PgDown scroll note  Home/End jump  a annotate run  N/Esc back to main  h/? more help"
		}
		return strings.Join([]string{
			title,
			"",
			fmt.Sprintf("%-12s %s", "navigation:", "Up/Down select run  N or Esc return to main view"),
			fmt.Sprintf("%-12s %s", "note:", "PgUp/PgDown or Ctrl+U/Ctrl+D scroll  Home/End jump"),
			fmt.Sprintf("%-12s %s", "edit:", "a annotate selected run"),
			fmt.Sprintf("%-12s %s", "help:", "h/? hide this help  q return to main"),
		}, "\n")
	}
	if !m.showHelp {
		return title + "\n\nUp/Down move  Enter launch  s stop  S kill  r restart  e edit  n new  d duplicate  a annotate run  N notes view  v command  c copy command  l autoscroll  h/? more help  q quit"
	}
	return strings.Join([]string{
		title,
		"",
		fmt.Sprintf("%-12s %s", "navigation:", "Up/Down select profile  Enter launch  q quit"),
		fmt.Sprintf("%-12s %s", "server:", "s stop gracefully  S force kill  r restart  l toggle log autoscroll"),
		fmt.Sprintf("%-12s %s", "log:", "when autoscroll=false, PgUp/PgDown or Ctrl+U/Ctrl+D scroll; Home/End jump"),
		fmt.Sprintf("%-12s %s", "profile:", "e edit selected  n new profile  d duplicate selected  a annotate latest run  N note view  v show command  c copy command"),
		fmt.Sprintf("%-12s %s", "help:", "h/? hide this help"),
	}, "\n")
}

func renderHistorySummary(summary history.ProfileSummary) string {
	if summary.ProfileName == "" {
		return ""
	}
	fields := []statusField{
		{label: "History", value: fmt.Sprintf("%d run(s)", summary.RunCount)},
	}
	if summary.GenerationSpeed.Count > 0 {
		fields = append(fields, statusField{
			label: "Gen tok/s",
			value: fmt.Sprintf(
				"latest %.2f  avg %.2f  median %.2f  range %.2f..%.2f  %s",
				summary.GenerationSpeed.Latest,
				summary.GenerationSpeed.Average,
				summary.GenerationSpeed.Median,
				summary.GenerationSpeed.Min,
				summary.GenerationSpeed.Max,
				history.Sparkline(summary.GenerationSpeed.Series),
			),
		})
	}
	if summary.PromptSpeed.Count > 0 {
		fields = append(fields, statusField{
			label: "Ingest tok/s",
			value: fmt.Sprintf(
				"latest %.2f  avg %.2f  median %.2f  range %.2f..%.2f  %s",
				summary.PromptSpeed.Latest,
				summary.PromptSpeed.Average,
				summary.PromptSpeed.Median,
				summary.PromptSpeed.Min,
				summary.PromptSpeed.Max,
				history.Sparkline(summary.PromptSpeed.Series),
			),
		})
	}
	return renderStatusFields(fields)
}

func renderStatusFields(fields []statusField) string {
	if len(fields) == 0 {
		return ""
	}
	maxLabelWidth := 0
	for _, field := range fields {
		if len(field.label) > maxLabelWidth {
			maxLabelWidth = len(field.label)
		}
	}
	var b strings.Builder
	for _, field := range fields {
		if strings.TrimSpace(field.value) == "" {
			continue
		}
		b.WriteString(fmt.Sprintf("%-*s: %s\n", maxLabelWidth, field.label, field.value))
	}
	return b.String()
}

func layoutHeights(height int, showHelp bool) (topH, statusH, keysH int) {
	if height <= 0 {
		return 1, 1, 1
	}
	if height <= 12 {
		keysMin := 3
		if showHelp {
			keysMin = 4
		}
		topH = max(4, height-4)
		keysH = min(keysMin, max(1, height-topH-1))
		statusH = max(1, height-topH-keysH)
		keysH = max(1, height-topH-statusH)
		return topH, statusH, keysH
	}

	topH = max(8, int(float64(height)*0.60))
	statusH = max(6, int(float64(height)*0.25))
	keysH = max(4, height-topH-statusH)
	if !showHelp || keysH >= 7 {
		return topH, statusH, keysH
	}

	deficit := 7 - keysH
	reduceStatus := min(deficit, statusH-6)
	statusH -= reduceStatus
	deficit -= reduceStatus

	reduceTop := min(deficit, topH-8)
	topH -= reduceTop

	return topH, statusH, height - topH - statusH
}

func computeMainLayout(width, height int, showHelp bool) mainLayoutSpec {
	topH, statusH, keysH := layoutHeights(height, showHelp)
	leftW, rightW := splitColumns(width, 0.30, 24, 40)

	layout := mainLayoutSpec{
		topH:    topH,
		statusH: statusH,
		keysH:   keysH,
		leftW:   leftW,
		rightW:  rightW,
		logsH:   topH,
	}

	if width >= 84 {
		layout.profilesH = topH
		return layout
	}

	layout.stacked = true
	layout.leftW = width
	layout.rightW = width
	layout.profilesH = min(max(6, topH/3), max(6, topH-6))
	layout.logsH = max(6, topH-layout.profilesH)
	layout.profilesH = max(4, topH-layout.logsH)
	return layout
}

func splitColumns(width int, leftRatio float64, leftMin, rightMin int) (leftW, rightW int) {
	if width <= 1 {
		return 1, 1
	}

	leftW = int(float64(width) * leftRatio)
	leftW = max(1, leftW)
	rightW = width - leftW

	if width >= leftMin+rightMin {
		leftW = max(leftMin, leftW)
		rightW = max(rightMin, width-leftW)
		leftW = width - rightW
		return max(1, leftW), max(1, rightW)
	}

	rightTarget := max(width/2, width-leftMin)
	rightW = min(width-1, max(1, rightTarget))
	leftW = max(1, width-rightW)
	return leftW, rightW
}

func colorizeLogLine(line string) string {
	if _, _, ok := parserHint(line); ok {
		return warnStyle.Render(line)
	}
	lower := strings.ToLower(line)
	switch {
	case strings.Contains(lower, "error") || strings.Contains(lower, "failed"):
		return errStyle.Render(line)
	case strings.Contains(lower, "warning") || strings.Contains(lower, "warn"):
		return warnStyle.Render(line)
	case strings.Contains(lower, "tokens per second"):
		return okStyle.Render(line)
	case strings.Contains(lower, "offloaded"):
		return blueStyle.Render(line)
	case strings.Contains(lower, "progress"):
		return dimStyle.Render(line)
	default:
		return line
	}
}

func parserHint(line string) (kind string, message string, ok bool) {
	parsed := parser.ParseLine(line)
	if parsed.HintMessage == "" {
		return "", "", false
	}
	return parsed.HintKind, parsed.HintMessage, true
}
