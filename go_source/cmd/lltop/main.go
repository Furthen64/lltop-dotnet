package main

import (
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"github.com/Furthen64/lltop/internal/config"
	"github.com/Furthen64/lltop/internal/history"
	"github.com/Furthen64/lltop/internal/parser"
	"github.com/Furthen64/lltop/internal/runner"
	"github.com/Furthen64/lltop/internal/ui"
	tea "github.com/charmbracelet/bubbletea"
)

func main() {
	var listProfiles bool
	var showCommand string
	var validateName string
	var runName string
	var refreshModels bool

	flag.BoolVar(&listProfiles, "list-profiles", false, "list profile names and exit")
	flag.StringVar(&showCommand, "show-command", "", "print generated command for a profile")
	flag.StringVar(&validateName, "validate", "", "validate a profile")
	flag.StringVar(&runName, "run", "", "run a profile headlessly")
	flag.BoolVar(&refreshModels, "refresh-models", false, "scan for new models and optionally create profiles")
	flag.Parse()

	cfg, created, err := config.LoadGlobalConfig()
	if err != nil {
		fatal(err)
	}

	status := ""
	if created {
		status, err = config.RunFirstStartWizard(cfg)
		if err != nil {
			fatal(err)
		}
	}

	profiles, err := config.LoadProfiles(cfg.ProfilesDir)
	if err != nil {
		fatal(err)
	}

	switch {
	case listProfiles:
		for _, profile := range profiles {
			fmt.Println(profile.Name)
		}
		return
	case showCommand != "":
		profile := mustProfile(profiles, showCommand)
		spec, err := runner.BuildCommand(cfg, profile)
		if err != nil {
			fatal(err)
		}
		fmt.Println(spec.Display)
		return
	case validateName != "":
		profile := mustProfile(profiles, validateName)
		if err := config.ValidateLaunchProfile(cfg, profile); err != nil {
			fatal(err)
		}
		fmt.Printf("profile %s is valid\n", profile.Name)
		return
	case runName != "":
		profile := mustProfile(profiles, runName)
		exitCode, err := runHeadless(cfg, profile)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
		}
		os.Exit(exitCode)
	case refreshModels:
		if cfg.ModelsDir == "" {
			fatal(fmt.Errorf("models_dir is not set in config; cannot scan for models"))
		}
		models, err := config.DiscoverModelFiles(cfg.ModelsDir, 3)
		if err != nil {
			fatal(err)
		}
		if len(models) == 0 {
			fmt.Println("No model files found.")
			return
		}

		existingModelPaths := map[string]bool{}
		for _, p := range profiles {
			if p.Model != "" {
				existingModelPaths[p.Model] = true
			}
		}

		var newModels []string
		for _, m := range models {
			if !existingModelPaths[m] {
				newModels = append(newModels, m)
			}
		}

		if len(newModels) == 0 {
			fmt.Println("No new models found. All discovered models already have profiles.")
			return
		}

		existingSlugs := map[string]struct{}{}
		for _, p := range profiles {
			existingSlugs[p.Name] = struct{}{}
		}

		created := 0
		for _, m := range newModels {
			baseName := strings.TrimSuffix(filepath.Base(m), filepath.Ext(m))
			slug := config.UniqueProfileSlug(config.SlugifyName(baseName), existingSlugs)
			existingSlugs[slug] = struct{}{}

			fmt.Printf("Found new model: %s\n", m)
			fmt.Printf("Create standard profile %q? [Y/n] ", slug)

			var response string
			_, scanErr := fmt.Scanln(&response)
			if scanErr == nil {
				response = strings.TrimSpace(strings.ToLower(response))
			}
			if response == "n" || response == "no" {
				fmt.Println("  -> Skipped")
				continue
			}

			profile := config.DefaultProfile(cfg, slug)
			profile.Description = "Auto-generated from refresh"
			profile.Model = m
			profilePath := filepath.Join(cfg.ProfilesDir, slug+".toml")
			if err := config.SaveProfile(profilePath, profile); err != nil {
				fmt.Fprintf(os.Stderr, "error creating profile for %s: %v\n", m, err)
				continue
			}
			fmt.Printf("  -> Created profile %q\n", slug)
			created++
		}
		fmt.Printf("Created %d new profile(s).\n", created)
		return
	default:
		program := tea.NewProgram(ui.NewModel(cfg, profiles, status), tea.WithAltScreen())
		if _, err := program.Run(); err != nil {
			fatal(err)
		}
	}
}

func runHeadless(cfg *config.GlobalConfig, profile *config.Profile) (int, error) {
	r := runner.New()
	spec, err := runner.BuildCommand(cfg, profile)
	if err != nil {
		return 1, err
	}
	if err := r.Launch(cfg, profile); err != nil {
		return 1, err
	}

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(sigCh)

	type stats struct {
		promptTokS float64
		evalTokS   float64
		genTokens  int
		promptTok  int
		offloaded  int
		total      int
		gpuTotal   int
		gpuFree    int
		gpuModel   int
		gpuCtx     int
		gpuCompute int
		issues     []history.Issue
	}
	snap := stats{}

	for {
		select {
		case <-sigCh:
			_ = r.Stop()
		case line, ok := <-r.LogCh:
			if !ok {
				r.LogCh = nil
				continue
			}
			fmt.Println(line)
			parsed := parser.ParseLine(line)
			if parsed.PromptTokensPerSec > 0 {
				snap.promptTokS = parsed.PromptTokensPerSec
				snap.promptTok = parsed.PromptTokens
			}
			if parsed.EvalTokensPerSec > 0 {
				snap.evalTokS = parsed.EvalTokensPerSec
				snap.genTokens = parsed.EvalTokens
			}
			if parsed.OffloadedLayers > 0 || parsed.TotalLayers > 0 {
				snap.offloaded = parsed.OffloadedLayers
				snap.total = parsed.TotalLayers
			}
			if parsed.GPUTotalMiB > 0 {
				snap.gpuTotal = parsed.GPUTotalMiB
				snap.gpuFree = parsed.GPUFreeMiB
				snap.gpuModel = parsed.GPUModelMiB
				snap.gpuCtx = parsed.GPUContextMiB
				snap.gpuCompute = parsed.GPUComputeMiB
			}
			if parsed.IsError {
				snap.issues = append(snap.issues, history.Issue{
					Severity:   "error",
					Kind:       parsed.ErrorKind,
					Message:    parsed.ErrorMessage,
					SeenAtSecs: time.Since(r.StartTime).Seconds(),
				})
			}
		case done := <-r.DoneCh:
			end := time.Now()
			exitReason := "exit"
			if done.Err != nil {
				exitReason = done.Err.Error()
			}
			record := history.NewRunRecord(cfg, profile, spec.Display, r.StartTime, end, done.ExitCode, exitReason, history.StatsSnapshot{
				PromptTokensPerSec: snap.promptTokS,
				EvalTokensPerSec:   snap.evalTokS,
				GeneratedTokens:    snap.genTokens,
				PromptTokens:       snap.promptTok,
				OffloadedLayers:    snap.offloaded,
				TotalLayers:        snap.total,
				GPUTotalMiB:        snap.gpuTotal,
				GPUFreeMiB:         snap.gpuFree,
				GPUModelMiB:        snap.gpuModel,
				GPUContextMiB:      snap.gpuCtx,
				GPUComputeMiB:      snap.gpuCompute,
				Issues:             snap.issues,
			})
			if _, err := history.SaveRunRecord(cfg.RunsDir, record); err != nil {
				return done.ExitCode, err
			}
			return done.ExitCode, done.Err
		}
	}
}

func mustProfile(profiles []*config.Profile, name string) *config.Profile {
	profile := config.FindProfile(profiles, name)
	if profile == nil {
		fatal(fmt.Errorf("profile %q not found", name))
	}
	return profile
}

func fatal(err error) {
	if err == nil {
		return
	}
	msg := err.Error()
	if !strings.HasSuffix(msg, "\n") {
		msg += "\n"
	}
	_, _ = os.Stderr.WriteString(msg)
	os.Exit(1)
}
