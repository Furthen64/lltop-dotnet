package runner

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
	"time"

	"github.com/Furthen64/lltop/internal/config"
)

const LogChBuffer = 1024

const (
	StatusStopped  = "stopped"
	StatusRunning  = "running"
	StatusStopping = "stopping"
	StatusFailed   = "failed"
)

type Runner struct {
	Profile   *config.Profile
	PID       int
	StartTime time.Time
	Status    string
	ExitCode  int
	LogLines  []string
	LogFile   *os.File
	LogCh     chan string
	DoneCh    chan ExitInfo

	mu        sync.Mutex
	cmd       *exec.Cmd
	logPath   string
	command   string
	closeOnce sync.Once
}

type ExitInfo struct {
	ExitCode int
	Err      error
}

func New() *Runner {
	return &Runner{
		Status:   StatusStopped,
		LogCh:    make(chan string, LogChBuffer),
		DoneCh:   make(chan ExitInfo, 1),
		LogLines: make([]string, 0, config.MaxLogLines),
	}
}

func (r *Runner) Launch(cfg *config.GlobalConfig, profile *config.Profile) error {
	if err := config.ValidateLaunchProfile(cfg, profile); err != nil {
		return err
	}

	spec, err := BuildCommand(cfg, profile)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(cfg.LogsDir, 0o755); err != nil {
		return err
	}

	r.mu.Lock()
	if r.Status == StatusRunning || r.Status == StatusStopping {
		r.mu.Unlock()
		return fmt.Errorf("runner is already active")
	}
	r.LogCh = make(chan string, LogChBuffer)
	r.DoneCh = make(chan ExitInfo, 1)
	r.LogLines = r.LogLines[:0]
	r.ExitCode = 0
	r.closeOnce = sync.Once{}
	r.mu.Unlock()

	logName := fmt.Sprintf("%s_%s.log", time.Now().Format("2006-01-02_150405"), config.SlugifyName(profile.Name))
	logPath := filepath.Join(cfg.LogsDir, logName)
	logFile, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}

	cmd := exec.Command(spec.Path, spec.Args...)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		logFile.Close()
		return err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		logFile.Close()
		return err
	}

	if err := cmd.Start(); err != nil {
		logFile.Close()
		return err
	}

	r.mu.Lock()
	r.Profile = profile
	r.PID = cmd.Process.Pid
	r.StartTime = time.Now()
	r.Status = StatusRunning
	r.LogFile = logFile
	r.cmd = cmd
	r.logPath = logPath
	r.command = spec.Display
	r.mu.Unlock()

	var wg sync.WaitGroup
	wg.Add(2)
	go r.readPipe(&wg, stdout)
	go r.readPipe(&wg, stderr)

	go func() {
		err := cmd.Wait()
		wg.Wait()
		exitCode := exitCodeFromErr(err)

		r.mu.Lock()
		r.ExitCode = exitCode
		if exitCode == 0 {
			r.Status = StatusStopped
		} else {
			r.Status = StatusFailed
		}
		r.PID = 0
		if r.LogFile != nil {
			r.LogFile.Close()
			r.LogFile = nil
		}
		r.cmd = nil
		r.mu.Unlock()

		r.DoneCh <- ExitInfo{ExitCode: exitCode, Err: err}
		r.closeOnce.Do(func() { close(r.LogCh) })
	}()

	return nil
}

func (r *Runner) readPipe(wg *sync.WaitGroup, pipe io.ReadCloser) {
	defer wg.Done()
	scanner := bufio.NewScanner(pipe)
	scanner.Buffer(make([]byte, 0, 64*1024), 1024*1024)
	for scanner.Scan() {
		r.appendLog(scanner.Text())
	}
	if err := scanner.Err(); err != nil {
		r.appendLog("scanner error: " + err.Error())
	}
}

func (r *Runner) appendLog(line string) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.LogFile != nil {
		_, _ = r.LogFile.WriteString(line + "\n")
	}
	r.LogLines = append(r.LogLines, line)
	if len(r.LogLines) > config.MaxLogLines {
		r.LogLines = append([]string(nil), r.LogLines[len(r.LogLines)-config.MaxLogLines:]...)
	}
	select {
	case r.LogCh <- line:
	default:
	}
}

func (r *Runner) Stop() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.cmd == nil || r.cmd.Process == nil {
		return fmt.Errorf("runner is not running")
	}
	r.Status = StatusStopping
	return sendInterrupt(r.cmd.Process)
}

func (r *Runner) Kill() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.cmd == nil || r.cmd.Process == nil {
		return fmt.Errorf("runner is not running")
	}
	r.Status = StatusStopping
	return sendKill(r.cmd.Process)
}

func (r *Runner) IsRunning() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.Status == StatusRunning || r.Status == StatusStopping
}

func (r *Runner) CommandString() string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.command
}

func (r *Runner) LogPath() string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.logPath
}

func exitCodeFromErr(err error) int {
	if err == nil {
		return 0
	}
	var exitErr *exec.ExitError
	if errors.As(err, &exitErr) {
		return exitErr.ExitCode()
	}
	return 1
}
