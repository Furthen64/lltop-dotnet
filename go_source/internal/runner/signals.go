package runner

import (
	"fmt"
	"os"
	"os/exec"
	"runtime"
	"syscall"
)

func sendInterrupt(process *os.Process) error {
	if runtime.GOOS == "windows" {
		cmd := exec.Command("taskkill", "/PID", fmt.Sprintf("%d", process.Pid))
		return cmd.Run()
	}
	return process.Signal(syscall.SIGINT)
}

func sendKill(process *os.Process) error {
	return process.Kill()
}
