package ui

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const (
	externalLogTailLines = 200
	externalLogTailBytes = 256 * 1024
)

type externalLogPollMsg struct {
	proc    externalProcess
	logPath string
	lines   []string
	offset  int64
}

func pollExternalLog(selfPID int, logsDir, currentPath string, currentOffset int64) externalLogPollMsg {
	proc, err := detectExternalLlamaServer(selfPID)
	if err != nil || proc.PID <= 0 {
		return externalLogPollMsg{}
	}

	logPath, err := resolveExternalProcessLogPath(proc.PID, logsDir)
	if err != nil || logPath == "" {
		return externalLogPollMsg{proc: proc}
	}

	if logPath != currentPath {
		lines, offset, err := tailFileLines(logPath, externalLogTailLines)
		if err != nil {
			return externalLogPollMsg{proc: proc, logPath: logPath}
		}
		return externalLogPollMsg{
			proc:    proc,
			logPath: logPath,
			lines:   lines,
			offset:  offset,
		}
	}

	lines, offset, err := readFileLinesFromOffset(logPath, currentOffset)
	if err != nil {
		return externalLogPollMsg{proc: proc, logPath: logPath}
	}
	return externalLogPollMsg{
		proc:    proc,
		logPath: logPath,
		lines:   lines,
		offset:  offset,
	}
}

func resolveExternalProcessLogPath(pid int, logsDir string) (string, error) {
	if pid <= 0 {
		return "", nil
	}
	candidates := make([]string, 0, 3)
	for _, fd := range []int{1, 2} {
		link, err := os.Readlink(fmt.Sprintf("/proc/%d/fd/%d", pid, fd))
		if err != nil {
			continue
		}
		if candidate, ok := normalizeLogPathCandidate(link); ok && isRegularFile(candidate) {
			candidates = append(candidates, candidate)
		}
	}
	if len(candidates) == 0 {
		if logsDir == "" {
			return "", nil
		}
		return latestLogFile(logsDir)
	}
	sort.Slice(candidates, func(i, j int) bool {
		left, errLeft := os.Stat(candidates[i])
		right, errRight := os.Stat(candidates[j])
		if errLeft != nil || errRight != nil {
			return candidates[i] < candidates[j]
		}
		return left.ModTime().After(right.ModTime())
	})
	return candidates[0], nil
}

func latestLogFile(logsDir string) (string, error) {
	entries, err := filepath.Glob(filepath.Join(logsDir, "*.log"))
	if err != nil {
		return "", err
	}
	if len(entries) == 0 {
		return "", nil
	}
	sort.Strings(entries)
	return entries[len(entries)-1], nil
}

func normalizeLogPathCandidate(raw string) (string, bool) {
	if raw == "" {
		return "", false
	}
	raw = strings.TrimSpace(strings.TrimSuffix(raw, " (deleted)"))
	if raw == "" {
		return "", false
	}
	if strings.HasPrefix(raw, "pipe:[") || strings.HasPrefix(raw, "socket:[") || strings.HasPrefix(raw, "anon_inode:") {
		return "", false
	}
	if !filepath.IsAbs(raw) {
		return "", false
	}
	if strings.HasPrefix(raw, "/dev/") {
		return "", false
	}
	return raw, true
}

func isRegularFile(path string) bool {
	info, err := os.Stat(path)
	if err != nil {
		return false
	}
	return info.Mode().IsRegular()
}

func tailFileLines(path string, maxLines int) ([]string, int64, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, 0, err
	}
	size := info.Size()
	start := int64(0)
	if size > externalLogTailBytes {
		start = size - externalLogTailBytes
	}
	lines, _, err := readFileLines(path, start, true)
	if err != nil {
		return nil, 0, err
	}
	if maxLines > 0 && len(lines) > maxLines {
		lines = lines[len(lines)-maxLines:]
	}
	return lines, size, nil
}

func readFileLinesFromOffset(path string, offset int64) ([]string, int64, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, 0, err
	}
	size := info.Size()
	if offset < 0 || offset > size {
		offset = 0
	}
	if offset == size {
		return nil, size, nil
	}
	return readFileLines(path, offset, false)
}

func readFileLines(path string, offset int64, trimLeadingPartial bool) ([]string, int64, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, 0, err
	}
	defer file.Close()
	if _, err := file.Seek(offset, io.SeekStart); err != nil {
		return nil, 0, err
	}
	data, err := io.ReadAll(file)
	if err != nil {
		return nil, 0, err
	}
	lines := bytesToLines(data, trimLeadingPartial)
	return lines, offset + int64(len(data)), nil
}

func bytesToLines(data []byte, trimLeadingPartial bool) []string {
	if len(data) == 0 {
		return nil
	}
	text := string(data)
	if trimLeadingPartial {
		if idx := strings.IndexByte(text, '\n'); idx >= 0 {
			text = text[idx+1:]
		} else {
			return nil
		}
	}
	parts := strings.Split(text, "\n")
	if len(parts) > 0 && parts[len(parts)-1] == "" {
		parts = parts[:len(parts)-1]
	}
	return parts
}
