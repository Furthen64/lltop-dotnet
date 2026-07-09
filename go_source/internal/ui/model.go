package ui

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/Furthen64/lltop/internal/config"
	"github.com/Furthen64/lltop/internal/history"
	"github.com/Furthen64/lltop/internal/parser"
	"github.com/Furthen64/lltop/internal/runner"
	"github.com/atotto/clipboard"
	"github.com/charmbracelet/bubbles/viewport"
	tea "github.com/charmbracelet/bubbletea"
)

type viewMode string

const (
	mainView  viewMode = "main"
	notesView viewMode = "notes"
)

type Model struct {
	cfg             *config.GlobalConfig
	profiles        []*config.Profile
	selectedIdx     int
	runner          *runner.Runner
	logViewport     viewport.Model
	noteViewport    viewport.Model
	stats           ServerStats
	width           int
	height          int
	viewMode        viewMode
	showHelp        bool
	confirmMode     bool
	confirmPrompt   string
	confirmAction   func()
	statusMsg       string
	logAutoScroll   bool
	historySummary  history.ProfileSummary
	profileRunState map[string]bool

	logLines        []string
	issues          []history.Issue
	pendingQuit     bool
	afterStop       func() tea.Cmd
	currentCommand  string
	openedProfile   string
	editorMode      string
	annotationPath  string
	annotationRun   string
	noteEntries     []history.RunRecordRef
	noteSelectedIdx int
	externalProc    externalProcess
	externalLog     string
	externalOffset  int64
}

type ServerStats struct {
	PromptTokensPerSec  float64
	EvalTokensPerSec    float64
	OffloadedLayers     int
	TotalLayers         int
	Progress            float64
	ChatFormat          string
	CtxSlotSize         int
	LastError           string
	LastGeneratedTokens int
	LastPromptTokens    int
	GPUTotalMiB         int
	GPUFreeMiB          int
	GPUModelMiB         int
	GPUContextMiB       int
	GPUComputeMiB       int
	LastHint            string
}

type logMsg string
type runnerDoneMsg struct{ info runner.ExitInfo }
type editorDoneMsg struct{ err error }

func NewModel(cfg *config.GlobalConfig, profiles []*config.Profile, statusMsg string) *Model {
	logVP := viewport.New(0, 0)
	logVP.SetContent("")
	noteVP := viewport.New(0, 0)
	noteVP.SetContent("")
	m := &Model{
		cfg:             cfg,
		profiles:        profiles,
		runner:          runner.New(),
		logViewport:     logVP,
		noteViewport:    noteVP,
		viewMode:        mainView,
		logAutoScroll:   true,
		statusMsg:       statusMsg,
		logLines:        []string{},
		issues:          []history.Issue{},
		profileRunState: map[string]bool{},
	}
	m.refreshRunHistoryState()
	return m
}

func (m *Model) Init() tea.Cmd {
	return waitForExternalLog(m)
}

func (m *Model) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {
	case tea.WindowSizeMsg:
		m.width = msg.Width
		m.height = msg.Height
		m.updateLayout()
		return m, nil
	case tea.KeyMsg:
		if m.confirmMode {
			switch msg.String() {
			case "y", "Y":
				m.confirmMode = false
				action := m.confirmAction
				m.confirmAction = nil
				if action != nil {
					action()
				}
				return m, m.followUpCmd()
			case "n", "N", "esc":
				m.confirmMode = false
				m.confirmAction = nil
				m.statusMsg = "Cancelled."
				return m, nil
			}
			return m, nil
		}

		if m.handleLogScrollKey(msg.String()) {
			return m, nil
		}
		if m.handleNoteViewKey(msg.String()) {
			return m, nil
		}

		switch msg.String() {
		case "ctrl+c":
			return m, tea.Quit
		case "up":
			if m.selectedIdx > 0 {
				m.selectedIdx--
			}
			m.refreshHistorySummary()
		case "down":
			if m.selectedIdx < len(m.profiles)-1 {
				m.selectedIdx++
			}
			m.refreshHistorySummary()
		case "enter":
			return m, m.launchSelectedCmd(false)
		case "s":
			if m.runner.IsRunning() {
				if err := m.runner.Stop(); err != nil {
					m.statusMsg = err.Error()
				} else {
					m.statusMsg = "Sent SIGINT."
				}
			}
		case "S":
			if m.runner.IsRunning() {
				if err := m.runner.Kill(); err != nil {
					m.statusMsg = err.Error()
				} else {
					m.statusMsg = "Sent SIGKILL."
				}
			}
		case "r":
			if m.runner.IsRunning() && m.cfg.ConfirmRestart {
				m.confirmPrompt = "Restart current server? [y/N]"
				m.confirmMode = true
				m.confirmAction = func() {
					m.afterStop = func() tea.Cmd { return m.launchSelectedCmd(true) }
					_ = m.runner.Stop()
					m.statusMsg = "Stopping current server for restart..."
				}
				return m, nil
			}
			return m, m.restartNowCmd()
		case "e":
			return m, m.editSelectedCmd()
		case "n":
			return m, m.newProfileCmd()
		case "d":
			if err := m.duplicateSelected(); err != nil {
				m.statusMsg = err.Error()
			} else {
				m.statusMsg = "Profile duplicated."
			}
			return m, nil
		case "v":
			if profile := m.selectedProfile(); profile != nil {
				spec, err := runner.BuildCommand(m.cfg, profile)
				if err != nil {
					m.statusMsg = err.Error()
				} else {
					m.statusMsg = spec.Display
				}
			}
		case "c":
			text, err := m.currentLaunchText()
			if err != nil {
				m.statusMsg = err.Error()
			} else if err := clipboard.WriteAll(text); err != nil {
				m.statusMsg = "clipboard failed: " + err.Error()
			} else {
				m.statusMsg = "Copied launch command to clipboard."
			}
		case "a":
			if m.viewMode == notesView {
				return m, m.annotateSelectedNoteCmd()
			}
			return m, m.annotateSelectedRunCmd()
		case "N":
			return m, m.enterNotesViewCmd()
		case "l":
			m.logAutoScroll = !m.logAutoScroll
			if m.logAutoScroll {
				m.logViewport.GotoBottom()
			}
			m.statusMsg = fmt.Sprintf("Log auto-scroll = %t", m.logAutoScroll)
		case "h", "?":
			m.showHelp = !m.showHelp
			m.updateLayout()
		case "q":
			if m.runner.IsRunning() {
				m.confirmMode = true
				m.confirmPrompt = "Server is running and cannot be detached here. Stop it and quit? [y/N]"
				m.confirmAction = func() {
					m.pendingQuit = true
					_ = m.runner.Stop()
					m.statusMsg = "Stopping server before quit..."
				}
				return m, nil
			}
			return m, tea.Quit
		}
	case logMsg:
		line := string(msg)
		m.logLines = append(m.logLines, line)
		if len(m.logLines) > config.MaxLogLines {
			m.logLines = append([]string(nil), m.logLines[len(m.logLines)-config.MaxLogLines:]...)
		}
		m.consumeParsedLine(line)
		m.refreshViewport()
		return m, waitForLog(m.runner)
	case externalLogPollMsg:
		if m.runner.IsRunning() {
			return m, waitForExternalLog(m)
		}
		if msg.proc.PID > 0 {
			m.externalProc = msg.proc
		} else {
			m.externalProc = externalProcess{}
		}
		if msg.logPath != m.externalLog {
			m.externalLog = msg.logPath
			m.externalOffset = 0
			m.stats = ServerStats{}
			m.issues = nil
			m.logLines = nil
		}
		if msg.logPath != "" {
			m.externalOffset = msg.offset
		}
		for _, line := range msg.lines {
			m.logLines = append(m.logLines, line)
			if len(m.logLines) > config.MaxLogLines {
				m.logLines = append([]string(nil), m.logLines[len(m.logLines)-config.MaxLogLines:]...)
			}
			m.consumeParsedLine(line)
		}
		if len(msg.lines) > 0 {
			m.refreshViewport()
		}
		return m, waitForExternalLog(m)
	case runnerDoneMsg:
		end := time.Now()
		started := m.runner.StartTime
		reason := "exit"
		if msg.info.Err != nil {
			reason = msg.info.Err.Error()
		}
		if profile := m.runner.Profile; profile != nil {
			snapshot := history.StatsSnapshot{
				PromptTokensPerSec: m.stats.PromptTokensPerSec,
				EvalTokensPerSec:   m.stats.EvalTokensPerSec,
				GeneratedTokens:    m.stats.LastGeneratedTokens,
				PromptTokens:       m.stats.LastPromptTokens,
				OffloadedLayers:    m.stats.OffloadedLayers,
				TotalLayers:        m.stats.TotalLayers,
				GPUTotalMiB:        m.stats.GPUTotalMiB,
				GPUFreeMiB:         m.stats.GPUFreeMiB,
				GPUModelMiB:        m.stats.GPUModelMiB,
				GPUContextMiB:      m.stats.GPUContextMiB,
				GPUComputeMiB:      m.stats.GPUComputeMiB,
				Issues:             append([]history.Issue(nil), m.issues...),
			}
			record := history.NewRunRecord(m.cfg, profile, m.currentCommand, started, end, msg.info.ExitCode, reason, snapshot)
			if _, err := history.SaveRunRecord(m.cfg.RunsDir, record); err != nil {
				m.statusMsg = "failed to store run record: " + err.Error()
			} else {
				m.refreshRunHistoryState()
				m.refreshNotesView()
				m.statusMsg = fmt.Sprintf("Run ended with exit code %d.", msg.info.ExitCode)
			}
		}
		if m.pendingQuit {
			return m, tea.Quit
		}
		if m.afterStop != nil {
			next := m.afterStop
			m.afterStop = nil
			return m, next()
		}
		return m, nil
	case editorDoneMsg:
		defer m.clearEditorState()
		if msg.err != nil {
			m.statusMsg = "editor failed: " + msg.err.Error()
			return m, nil
		}
		if m.editorMode == "annotate" {
			if err := m.saveEditedAnnotation(); err != nil {
				m.statusMsg = err.Error()
			} else {
				m.refreshHistorySummary()
				m.refreshNotesView()
				m.statusMsg = "Saved run note."
			}
			return m, nil
		}
		if err := m.reloadProfiles(); err != nil {
			m.statusMsg = err.Error()
		} else if m.openedProfile != "" {
			m.statusMsg = "Saved profile: " + m.openedProfile
		}
		return m, nil
	}

	return m, nil
}

func (m *Model) handleLogScrollKey(key string) bool {
	if m.viewMode != mainView {
		return false
	}
	if m.logAutoScroll {
		return false
	}

	switch key {
	case "pgup", "ctrl+u":
		m.logViewport.HalfPageUp()
	case "pgdown", "ctrl+d":
		m.logViewport.HalfPageDown()
	case "home":
		m.logViewport.GotoTop()
	case "end":
		m.logViewport.GotoBottom()
	default:
		return false
	}
	m.statusMsg = fmt.Sprintf("Log position %.0f%%", m.logViewport.ScrollPercent()*100)
	return true
}

func (m *Model) handleNoteViewKey(key string) bool {
	if m.viewMode != notesView {
		return false
	}

	switch key {
	case "esc", "q", "N":
		m.viewMode = mainView
		m.statusMsg = "Returned to main view."
		return true
	case "up":
		if m.noteSelectedIdx > 0 {
			m.noteSelectedIdx--
			m.refreshNoteViewport()
		}
		return true
	case "down":
		if m.noteSelectedIdx < len(m.noteEntries)-1 {
			m.noteSelectedIdx++
			m.refreshNoteViewport()
		}
		return true
	case "pgup", "ctrl+u":
		m.noteViewport.HalfPageUp()
		return true
	case "pgdown", "ctrl+d":
		m.noteViewport.HalfPageDown()
		return true
	case "home":
		m.noteViewport.GotoTop()
		return true
	case "end":
		m.noteViewport.GotoBottom()
		return true
	default:
		return false
	}
}

func (m *Model) currentLaunchText() (string, error) {
	if m.runner != nil && m.runner.IsRunning() && m.currentCommand != "" {
		return m.currentCommand, nil
	}
	if m.externalProc.Command != "" {
		return m.externalProc.Command, nil
	}
	if m.runner == nil || !m.runner.IsRunning() {
		if proc, err := detectExternalLlamaServer(os.Getpid()); err == nil && proc.Command != "" {
			return proc.Command, nil
		}
	}
	profile := m.selectedProfile()
	if profile == nil {
		return "", fmt.Errorf("no launch command available")
	}
	spec, err := runner.BuildCommand(m.cfg, profile)
	if err != nil {
		return "", err
	}
	return spec.Display, nil
}

func (m *Model) selectedProfile() *config.Profile {
	if len(m.profiles) == 0 || m.selectedIdx < 0 || m.selectedIdx >= len(m.profiles) {
		return nil
	}
	return m.profiles[m.selectedIdx]
}

func (m *Model) launchSelectedCmd(skipFailureCheck bool) tea.Cmd {
	profile := m.selectedProfile()
	if profile == nil {
		m.statusMsg = "No profile selected."
		return nil
	}
	if m.runner.IsRunning() {
		m.statusMsg = "A server is already running."
		return nil
	}
	if !skipFailureCheck && m.cfg.ConfirmRecentFailure {
		recent, err := history.FindRecentFailure(m.cfg.RunsDir, history.BuildScenarioKey(profile), m.cfg.RecentFailureWindowSecs, m.cfg.StartupFailureSecs)
		if err != nil {
			m.statusMsg = err.Error()
			return nil
		}
		if recent != nil {
			ago := int(time.Since(recent.StartedAt).Seconds())
			issue := "startup failure"
			if len(recent.Issues) > 0 {
				issue = recent.Issues[0].Kind
			}
			m.confirmMode = true
			m.confirmPrompt = fmt.Sprintf("This same scenario failed %ds ago (exit code %d).\nProfile: %s  Issue: %s\nRun it again? [y/N]", ago, recent.ExitCode, recent.ProfileName, issue)
			m.confirmAction = func() {
				m.afterStop = nil
				m.statusMsg = "Launching profile..."
				if err := m.startSelected(); err != nil {
					m.statusMsg = err.Error()
				}
			}
			return nil
		}
	}
	if err := m.startSelected(); err != nil {
		m.statusMsg = err.Error()
		return nil
	}
	return tea.Batch(waitForLog(m.runner), waitForDone(m.runner))
}

func (m *Model) restartNowCmd() tea.Cmd {
	if m.runner.IsRunning() {
		m.afterStop = func() tea.Cmd { return m.launchSelectedCmd(true) }
		if err := m.runner.Stop(); err != nil {
			m.statusMsg = err.Error()
			return nil
		}
		return waitForDone(m.runner)
	}
	return m.launchSelectedCmd(true)
}

func (m *Model) startSelected() error {
	profile := m.selectedProfile()
	if profile == nil {
		return fmt.Errorf("no profile selected")
	}
	m.stats = ServerStats{}
	m.issues = nil
	m.logLines = nil
	m.externalProc = externalProcess{}
	m.externalLog = ""
	m.externalOffset = 0
	m.refreshViewport()
	spec, err := runner.BuildCommand(m.cfg, profile)
	if err != nil {
		return err
	}
	m.currentCommand = spec.Display
	if err := m.runner.Launch(m.cfg, profile); err != nil {
		return err
	}
	m.statusMsg = "Launched " + profile.Name
	return nil
}

func (m *Model) consumeParsedLine(line string) {
	parsed := parser.ParseLine(line)
	if parsed.PromptTokensPerSec > 0 {
		m.stats.PromptTokensPerSec = parsed.PromptTokensPerSec
		m.stats.LastPromptTokens = parsed.PromptTokens
	}
	if parsed.EvalTokensPerSec > 0 {
		m.stats.EvalTokensPerSec = parsed.EvalTokensPerSec
		m.stats.LastGeneratedTokens = parsed.EvalTokens
	}
	if parsed.OffloadedLayers > 0 || parsed.TotalLayers > 0 {
		m.stats.OffloadedLayers = parsed.OffloadedLayers
		m.stats.TotalLayers = parsed.TotalLayers
	}
	if parsed.Progress > 0 {
		m.stats.Progress = parsed.Progress
	}
	if parsed.ChatFormat != "" {
		m.stats.ChatFormat = parsed.ChatFormat
	}
	if parsed.CtxSlotSize > 0 {
		m.stats.CtxSlotSize = parsed.CtxSlotSize
	}
	if parsed.GPUTotalMiB > 0 {
		m.stats.GPUTotalMiB = parsed.GPUTotalMiB
		m.stats.GPUFreeMiB = parsed.GPUFreeMiB
		m.stats.GPUModelMiB = parsed.GPUModelMiB
		m.stats.GPUContextMiB = parsed.GPUContextMiB
		m.stats.GPUComputeMiB = parsed.GPUComputeMiB
	}
	if parsed.HintMessage != "" {
		m.stats.LastHint = parsed.HintMessage
	}
	if parsed.IsError {
		m.stats.LastError = parsed.ErrorMessage
		seenAt := 0.0
		if !m.runner.StartTime.IsZero() {
			seenAt = time.Since(m.runner.StartTime).Seconds()
		}
		m.issues = append(m.issues, history.Issue{
			Severity:   "error",
			Kind:       parsed.ErrorKind,
			Message:    parsed.ErrorMessage,
			SeenAtSecs: seenAt,
		})
	}
}

func (m *Model) refreshViewport() {
	rendered := make([]string, 0, len(m.logLines))
	for _, line := range m.logLines {
		rendered = append(rendered, colorizeLogLine(line))
	}
	m.logViewport.SetContent(strings.Join(rendered, "\n"))
	if m.logAutoScroll {
		m.logViewport.GotoBottom()
	}
}

func (m *Model) updateLayout() {
	width := m.width
	if width <= 0 {
		width = 120
	}
	height := m.height
	if height <= 0 {
		height = 40
	}
	layout := computeMainLayout(width, height-1, m.showHelp)
	m.logViewport.Width = max(1, layout.rightW-6)
	m.logViewport.Height = max(1, layout.logsH-6)
	topH, _, _ := layoutHeights(height-1, m.showHelp)
	_, noteRightW := splitColumns(width, 0.35, 28, 40)
	m.noteViewport.Width = max(1, noteRightW-6)
	m.noteViewport.Height = max(1, topH-6)
	m.refreshViewport()
	m.refreshNoteViewport()
}

func (m *Model) reloadProfiles() error {
	profiles, err := config.LoadProfiles(m.cfg.ProfilesDir)
	if err != nil {
		return err
	}
	m.profiles = profiles
	if len(m.profiles) == 0 {
		m.selectedIdx = 0
		return nil
	}
	for i, profile := range m.profiles {
		if profile.Name == m.openedProfile {
			m.selectedIdx = i
			m.refreshHistorySummary()
			m.refreshNotesView()
			return nil
		}
	}
	if m.selectedIdx >= len(m.profiles) {
		m.selectedIdx = len(m.profiles) - 1
	}
	m.refreshHistorySummary()
	m.refreshNotesView()
	return nil
}

func (m *Model) editSelectedCmd() tea.Cmd {
	profile := m.selectedProfile()
	if profile == nil {
		m.statusMsg = "No profile selected."
		return nil
	}
	path := filepath.Join(m.cfg.ProfilesDir, config.SlugifyName(profile.Name)+".toml")
	m.openedProfile = profile.Name
	return openEditor(m.cfg.Editor, path)
}

func (m *Model) enterNotesViewCmd() tea.Cmd {
	profile := m.selectedProfile()
	if profile == nil {
		m.statusMsg = "No profile selected."
		return nil
	}
	m.viewMode = notesView
	if err := m.loadNoteEntries(profile.Name); err != nil {
		m.statusMsg = err.Error()
		return nil
	}
	if len(m.noteEntries) == 0 {
		m.statusMsg = fmt.Sprintf("No runs recorded for profile %s.", profile.Name)
	} else {
		m.statusMsg = fmt.Sprintf("Showing notes for %s.", profile.Name)
	}
	m.updateLayout()
	return nil
}

func (m *Model) newProfileCmd() tea.Cmd {
	base := "new-profile"
	file := base + ".toml"
	path := filepath.Join(m.cfg.ProfilesDir, file)
	for i := 2; ; i++ {
		if _, err := os.Stat(path); os.IsNotExist(err) {
			break
		}
		file = fmt.Sprintf("%s-%d.toml", base, i)
		path = filepath.Join(m.cfg.ProfilesDir, file)
	}
	name := strings.TrimSuffix(file, ".toml")
	profile := config.DefaultProfile(m.cfg, name)
	profile.Description = "New profile"
	if err := config.SaveProfile(path, profile); err != nil {
		m.statusMsg = err.Error()
		return nil
	}
	m.openedProfile = profile.Name
	return openEditor(m.cfg.Editor, path)
}

func (m *Model) duplicateSelected() error {
	profile := m.selectedProfile()
	if profile == nil {
		return fmt.Errorf("no profile selected")
	}
	dup := *profile
	base := profile.Name + "-copy"
	dup.Name = base
	path := filepath.Join(m.cfg.ProfilesDir, config.SlugifyName(dup.Name)+".toml")
	for i := 2; ; i++ {
		if _, err := os.Stat(path); os.IsNotExist(err) {
			break
		}
		dup.Name = fmt.Sprintf("%s-%d", base, i)
		path = filepath.Join(m.cfg.ProfilesDir, config.SlugifyName(dup.Name)+".toml")
	}
	if err := config.SaveProfile(path, &dup); err != nil {
		return err
	}
	m.openedProfile = dup.Name
	return m.reloadProfiles()
}

func (m *Model) annotateSelectedRunCmd() tea.Cmd {
	profile := m.selectedProfile()
	if profile == nil {
		m.statusMsg = "No profile selected."
		return nil
	}
	path, record, err := history.FindLatestRunRecordForProfile(m.cfg.RunsDir, profile.Name)
	if err != nil {
		m.statusMsg = err.Error()
		return nil
	}
	return m.annotateRunCmd(path, record)
}

func (m *Model) annotateSelectedNoteCmd() tea.Cmd {
	ref := m.selectedNoteRef()
	if ref == nil {
		m.statusMsg = "No run selected."
		return nil
	}
	return m.annotateRunCmd(ref.Path, ref.Record)
}

func (m *Model) annotateRunCmd(path string, record *history.RunRecord) tea.Cmd {
	if record == nil {
		m.statusMsg = "No run selected."
		return nil
	}
	tmp, err := os.CreateTemp("", "lltop-note-*.md")
	if err != nil {
		m.statusMsg = err.Error()
		return nil
	}
	body := record.Notes
	if body == "" {
		body = annotationTemplate(record.ProfileName, record)
	}
	if _, err := tmp.WriteString(body); err != nil {
		_ = tmp.Close()
		_ = os.Remove(tmp.Name())
		m.statusMsg = err.Error()
		return nil
	}
	if err := tmp.Close(); err != nil {
		_ = os.Remove(tmp.Name())
		m.statusMsg = err.Error()
		return nil
	}
	m.editorMode = "annotate"
	m.annotationPath = tmp.Name()
	m.annotationRun = path
	return openEditor(m.cfg.Editor, tmp.Name())
}

func (m *Model) saveEditedAnnotation() error {
	if m.annotationPath == "" || m.annotationRun == "" {
		return fmt.Errorf("annotation editor state missing")
	}
	data, err := os.ReadFile(m.annotationPath)
	if err != nil {
		return err
	}
	record, err := history.LoadRunRecord(m.annotationRun)
	if err != nil {
		return err
	}
	record.Notes = strings.TrimSpace(string(data))
	return history.UpdateRunRecord(m.annotationRun, record)
}

func (m *Model) clearEditorState() {
	if m.annotationPath != "" {
		_ = os.Remove(m.annotationPath)
	}
	m.editorMode = ""
	m.annotationPath = ""
	m.annotationRun = ""
}

func (m *Model) refreshHistorySummary() {
	profile := m.selectedProfile()
	if profile == nil || m.cfg == nil {
		m.historySummary = history.ProfileSummary{}
		return
	}
	records, err := history.LoadRunRecords(m.cfg.RunsDir)
	if err != nil {
		m.historySummary = history.ProfileSummary{ProfileName: profile.Name}
		return
	}
	m.historySummary = history.SummarizeProfileRuns(records, profile.Name)
}

func (m *Model) refreshRunHistoryState() {
	if m.cfg == nil {
		m.profileRunState = map[string]bool{}
		m.historySummary = history.ProfileSummary{}
		return
	}

	records, err := history.LoadRunRecords(m.cfg.RunsDir)
	if err != nil {
		m.profileRunState = map[string]bool{}
		if profile := m.selectedProfile(); profile != nil {
			m.historySummary = history.ProfileSummary{ProfileName: profile.Name}
		} else {
			m.historySummary = history.ProfileSummary{}
		}
		return
	}

	state := make(map[string]bool, len(records))
	for _, record := range records {
		if record == nil {
			continue
		}
		state[strings.ToLower(record.ProfileName)] = true
	}
	m.profileRunState = state
	m.refreshHistorySummary()
}

func (m *Model) loadNoteEntries(profileName string) error {
	entries, err := history.FindRunRecordsForProfile(m.cfg.RunsDir, profileName)
	if err != nil {
		return err
	}
	m.noteEntries = entries
	if len(entries) == 0 {
		m.noteSelectedIdx = 0
	} else if m.noteSelectedIdx >= len(entries) {
		m.noteSelectedIdx = len(entries) - 1
	}
	m.refreshNoteViewport()
	return nil
}

func (m *Model) refreshNotesView() {
	if m.viewMode != notesView {
		return
	}
	profile := m.selectedProfile()
	if profile == nil || m.cfg == nil {
		m.noteEntries = nil
		m.noteSelectedIdx = 0
		m.refreshNoteViewport()
		return
	}
	_ = m.loadNoteEntries(profile.Name)
}

func (m *Model) selectedNoteRef() *history.RunRecordRef {
	if len(m.noteEntries) == 0 || m.noteSelectedIdx < 0 || m.noteSelectedIdx >= len(m.noteEntries) {
		return nil
	}
	return &m.noteEntries[m.noteSelectedIdx]
}

func (m *Model) refreshNoteViewport() {
	ref := m.selectedNoteRef()
	if ref == nil || ref.Record == nil {
		m.noteViewport.SetContent(dimStyle.Render("No runs recorded for this profile yet."))
		m.noteViewport.GotoTop()
		return
	}
	content := strings.TrimSpace(ref.Record.Notes)
	if content == "" {
		content = strings.TrimSpace(annotationTemplate(ref.Record.ProfileName, ref.Record))
		content += "\n\n(no saved note yet; press 'a' to annotate this run)"
	}
	m.noteViewport.SetContent(content)
	m.noteViewport.GotoTop()
}

func annotationTemplate(profileName string, record *history.RunRecord) string {
	if record == nil {
		return ""
	}
	var b strings.Builder
	fmt.Fprintf(&b, "profile: %s\n", profileName)
	fmt.Fprintf(&b, "run_id: %s\n", record.RunID)
	fmt.Fprintf(&b, "started_at: %s\n", record.StartedAt.Format(time.RFC3339))
	fmt.Fprintf(&b, "duration_seconds: %.2f\n", record.DurationSeconds)
	fmt.Fprintf(&b, "exit_code: %d\n", record.ExitCode)
	fmt.Fprintf(&b, "exit_reason: %s\n", record.ExitReason)
	fmt.Fprintf(&b, "generation_tok_s: %.2f\n", record.LastEvalTokensPerSec)
	fmt.Fprintf(&b, "prompt_tok_s: %.2f\n", record.LastPromptTokensPerSec)
	b.WriteString("\nrun_parameters:\n")
	writeAnnotationParam(&b, "llama_server", record.LlamaServer)
	writeAnnotationParam(&b, "model", record.Model)
	writeAnnotationParam(&b, "host", record.Host)
	writeAnnotationParam(&b, "port", strconv.Itoa(record.Port))
	writeAnnotationParam(&b, "alias", record.Alias)
	writeAnnotationParam(&b, "ctx", strconv.Itoa(record.Ctx))
	writeAnnotationParam(&b, "ngl", strconv.Itoa(record.NGL))
	writeAnnotationParam(&b, "cache_k", record.CacheK)
	writeAnnotationParam(&b, "cache_v", record.CacheV)
	writeAnnotationParam(&b, "temp", strconv.FormatFloat(record.Temp, 'f', -1, 64))
	writeAnnotationParam(&b, "top_p", strconv.FormatFloat(record.TopP, 'f', -1, 64))
	writeAnnotationParam(&b, "top_k", strconv.Itoa(record.TopK))
	writeAnnotationParam(&b, "min_p", strconv.FormatFloat(record.MinP, 'f', -1, 64))
	writeAnnotationParam(&b, "batch", strconv.Itoa(record.Batch))
	writeAnnotationParam(&b, "ubatch", strconv.Itoa(record.UBatch))
	writeAnnotationParam(&b, "parallel", strconv.Itoa(record.Parallel))
	if record.Threads > 0 {
		writeAnnotationParam(&b, "threads", strconv.Itoa(record.Threads))
	}
	writeAnnotationParam(&b, "flash_attn", record.FlashAttn)
	writeAnnotationParam(&b, "reasoning", record.Reasoning)
	writeAnnotationParam(&b, "reasoning_budget", strconv.Itoa(record.ReasoningBudget))
	writeAnnotationParam(&b, "metrics", strconv.FormatBool(record.Metrics))
	writeAnnotationParam(&b, "jinja", strconv.FormatBool(record.Jinja))
	writeAnnotationParam(&b, "no_mmap", strconv.FormatBool(record.NoMmap))
	writeAnnotationParam(&b, "chat_template", record.ChatTemplate)
	if len(record.ExtraArgs) > 0 {
		writeAnnotationParam(&b, "extra_args", strings.Join(record.ExtraArgs, " "))
	}
	if record.GeneratedCommand != "" {
		fmt.Fprintf(&b, "\ncommand:\n%s\n", record.GeneratedCommand)
	}
	b.WriteString("\nnotes:\n")
	return b.String()
}

func writeAnnotationParam(b *strings.Builder, key, value string) {
	if value == "" {
		return
	}
	fmt.Fprintf(b, "%s: %s\n", key, value)
}

func waitForLog(r *runner.Runner) tea.Cmd {
	if r == nil {
		return nil
	}
	return func() tea.Msg {
		line, ok := <-r.LogCh
		if !ok {
			return nil
		}
		return logMsg(line)
	}
}

func waitForDone(r *runner.Runner) tea.Cmd {
	if r == nil {
		return nil
	}
	return func() tea.Msg {
		info := <-r.DoneCh
		return runnerDoneMsg{info: info}
	}
}

func waitForExternalLog(m *Model) tea.Cmd {
	if m == nil {
		return nil
	}
	return tea.Tick(time.Second, func(time.Time) tea.Msg {
		if m.runner != nil && m.runner.IsRunning() {
			return externalLogPollMsg{}
		}
		logsDir := ""
		if m.cfg != nil {
			logsDir = m.cfg.LogsDir
		}
		return pollExternalLog(os.Getpid(), logsDir, m.externalLog, m.externalOffset)
	})
}

func openEditor(editor, path string) tea.Cmd {
	parts := strings.Fields(editor)
	if len(parts) == 0 {
		parts = []string{config.DefaultEditor()}
	}
	cmd := exec.Command(parts[0], append(parts[1:], path)...)
	return tea.ExecProcess(cmd, func(err error) tea.Msg {
		return editorDoneMsg{err: err}
	})
}

func (m *Model) followUpCmd() tea.Cmd {
	if m.runner == nil {
		return nil
	}
	if m.pendingQuit && m.runner.IsRunning() {
		return tea.Batch(waitForLog(m.runner), waitForDone(m.runner))
	}
	if m.runner.IsRunning() {
		return tea.Batch(waitForLog(m.runner), waitForDone(m.runner))
	}
	return nil
}
