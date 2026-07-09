package config

import (
	"fmt"
	"runtime"
	"strings"

	"github.com/charmbracelet/bubbles/textinput"
	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/lipgloss"
)

var (
	wizardTitleStyle = lipgloss.NewStyle().Foreground(lipgloss.Color("86")).Bold(true)
	wizardLabelStyle = lipgloss.NewStyle().Foreground(lipgloss.Color("81")).Bold(true)
	wizardHintStyle  = lipgloss.NewStyle().Foreground(lipgloss.Color("241"))
	wizardErrStyle   = lipgloss.NewStyle().Foreground(lipgloss.Color("196"))
)

func runFirstRunWizardTUI(defaultLlama, defaultModelsDir string) (string, string, error) {
	m := newFirstRunWizardModel(defaultLlama, defaultModelsDir)
	finalModel, err := tea.NewProgram(m).Run()
	if err != nil {
		return "", "", err
	}
	done, ok := finalModel.(firstRunWizardModel)
	if !ok {
		return "", "", fmt.Errorf("unexpected wizard result type")
	}
	if done.cancelled {
		return "", "", fmt.Errorf("first-run setup cancelled")
	}
	return done.resolvedLlamaPath, done.resolvedModelsDir, nil
}

type firstRunWizardModel struct {
	input             textinput.Model
	step              int
	defaultLlama      string
	defaultModelsDir  string
	resolvedLlamaPath string
	resolvedModelsDir string
	errMsg            string
	cancelled         bool
}

func newFirstRunWizardModel(defaultLlama, defaultModelsDir string) firstRunWizardModel {
	input := textinput.New()
	input.Prompt = "› "
	input.Width = 80
	input.Focus()
	input.SetValue(defaultLlama)
	return firstRunWizardModel{
		input:             input,
		defaultLlama:      defaultLlama,
		defaultModelsDir:  defaultModelsDir,
		resolvedLlamaPath: defaultLlama,
		resolvedModelsDir: defaultModelsDir,
	}
}

func (m firstRunWizardModel) Init() tea.Cmd {
	return textinput.Blink
}

func (m firstRunWizardModel) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {
	case tea.KeyMsg:
		switch msg.String() {
		case "ctrl+c", "esc":
			m.cancelled = true
			return m, tea.Quit
		case "enter":
			if err := m.submitCurrentStep(); err != nil {
				m.errMsg = err.Error()
				return m, nil
			}
			if m.step > 1 {
				return m, tea.Quit
			}
			m.errMsg = ""
			m.input.SetValue(m.currentDefaultValue())
			m.input.CursorEnd()
			return m, nil
		}
	}
	var cmd tea.Cmd
	m.input, cmd = m.input.Update(msg)
	return m, cmd
}

func (m *firstRunWizardModel) submitCurrentStep() error {
	value := strings.TrimSpace(m.input.Value())
	switch m.step {
	case 0:
		resolved, err := validateWizardPathInput(value, m.defaultLlama, resolveLlamaServerPath)
		if err != nil {
			return err
		}
		m.resolvedLlamaPath = resolved
		m.step = 1
		return nil
	case 1:
		resolved, err := validateWizardPathInput(value, m.defaultModelsDir, func(path string) (string, error) {
			if err := optionalDirectory(path); err != nil {
				return "", err
			}
			return path, nil
		})
		if err != nil {
			return err
		}
		m.resolvedModelsDir = resolved
		m.step = 2
		return nil
	default:
		m.step = 3
		return nil
	}
}

func validateWizardPathInput(value, defaultValue string, validator func(string) (string, error)) (string, error) {
	if value == "" {
		value = defaultValue
	}
	expanded, err := ExpandPath(value)
	if err != nil {
		return "", fmt.Errorf("invalid path: %w", err)
	}
	validated, err := validator(expanded)
	if err != nil {
		return "", fmt.Errorf("invalid path: %w", err)
	}
	return validated, nil
}

func (m firstRunWizardModel) currentDefaultValue() string {
	if m.step == 0 {
		return m.defaultLlama
	}
	if m.step == 1 {
		return m.defaultModelsDir
	}
	return ""
}

func (m firstRunWizardModel) View() string {
	label, help := m.currentStepText()
	parts := []string{
		wizardTitleStyle.Render("Welcome to lltop first-run setup"),
		wizardHintStyle.Render("Press Enter to continue, Esc/Ctrl+C to cancel."),
		"",
		wizardLabelStyle.Render(label),
		m.input.View(),
		wizardHintStyle.Render(help),
	}
	if m.errMsg != "" {
		parts = append(parts, "", wizardErrStyle.Render(m.errMsg))
	}
	return strings.Join(parts, "\n")
}

func (m firstRunWizardModel) currentStepText() (string, string) {
	switch m.step {
	case 0:
		if runtime.GOOS == "windows" {
			return "Path to llama-server binary or directory", `Examples: C:\llama, %LOCALAPPDATA%\llama, C:\llama\llama-server.exe`
		}
		return "Path to llama-server binary or directory", "Examples: /usr/local/llama, ~/llama, /usr/bin/llama-server"
	case 1:
		if runtime.GOOS == "windows" {
			return "Path to models directory (optional)", `Leave empty to skip model discovery. Example: C:\Users\you\models`
		}
		return "Path to models directory (optional)", "Leave empty to skip model discovery."
	default:
		return "Saving first-run setup...", ""
	}
}
