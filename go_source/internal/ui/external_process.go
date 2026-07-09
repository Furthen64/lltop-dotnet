package ui

import (
	"encoding/csv"
	"io"
	"os/exec"
	"runtime"
	"strconv"
	"strings"
)

const llamaServerBinary = "llama-server"

type externalProcess struct {
	PID     int
	Command string
}

func detectExternalLlamaServer(selfPID int) (externalProcess, error) {
	if runtime.GOOS == "windows" {
		cmd := exec.Command("tasklist", "/FI", "IMAGENAME eq llama-server.exe", "/FO", "CSV", "/NH")
		out, err := cmd.Output()
		if err != nil {
			return externalProcess{}, err
		}
		proc, ok := parseExternalLlamaServerTasklist(string(out), selfPID)
		if !ok {
			return externalProcess{}, nil
		}
		return proc, nil
	}

	cmd := exec.Command("ps", "-eo", "pid=,comm=,args=")
	out, err := cmd.Output()
	if err != nil {
		return externalProcess{}, err
	}
	proc, ok := parseExternalLlamaServer(string(out), selfPID)
	if !ok {
		return externalProcess{}, nil
	}
	return proc, nil
}

func parseExternalLlamaServer(psOutput string, selfPID int) (externalProcess, bool) {
	lines := strings.Split(psOutput, "\n")
	for _, line := range lines {
		fields := strings.Fields(line)
		if len(fields) < 3 {
			continue
		}
		pid, err := strconv.Atoi(fields[0])
		if err != nil || pid <= 0 || pid == selfPID {
			continue
		}
		comm := fields[1]
		if comm != llamaServerBinary {
			continue
		}
		return externalProcess{
			PID:     pid,
			Command: strings.Join(fields[2:], " "),
		}, true
	}
	return externalProcess{}, false
}

func parseExternalLlamaServerTasklist(tasklistOutput string, selfPID int) (externalProcess, bool) {
	reader := csv.NewReader(strings.NewReader(tasklistOutput))
	for {
		record, err := reader.Read()
		if err == io.EOF {
			break
		}
		if err != nil || len(record) < 2 {
			continue
		}

		imageName := strings.TrimSpace(record[0])
		if !strings.EqualFold(imageName, "llama-server.exe") {
			continue
		}

		pid, err := strconv.Atoi(strings.TrimSpace(record[1]))
		if err != nil || pid <= 0 || pid == selfPID {
			continue
		}

		return externalProcess{
			PID:     pid,
			Command: imageName,
		}, true
	}

	return externalProcess{}, false
}
