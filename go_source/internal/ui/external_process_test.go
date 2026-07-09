package ui

import "testing"

func TestParseExternalLlamaServer_FindsServer(t *testing.T) {
	out := `125756 llama-server ../../app/llama-server -m ../../models/Qwen3-4B-Q8_0.gguf --port 8080
130001 bash bash -lc ps -eo pid=,comm=,args=
`
	got, ok := parseExternalLlamaServer(out, 130001)
	if !ok {
		t.Fatal("expected external llama-server to be found")
	}
	if got.PID != 125756 {
		t.Fatalf("expected pid 125756, got %d", got.PID)
	}
	if got.Command == "" {
		t.Fatal("expected command to be captured")
	}
}

func TestParseExternalLlamaServer_IgnoresSelfPID(t *testing.T) {
	out := `130001 llama-server ../../app/llama-server -m model.gguf --port 8080
`
	_, ok := parseExternalLlamaServer(out, 130001)
	if ok {
		t.Fatal("expected self pid process to be ignored")
	}
}

func TestParseExternalLlamaServer_NoMatch(t *testing.T) {
	out := `1 systemd /sbin/init
2 kthreadd [kthreadd]
`
	_, ok := parseExternalLlamaServer(out, 99999)
	if ok {
		t.Fatal("expected no external llama-server match")
	}
}

func TestParseExternalLlamaServerTasklist_FindsServer(t *testing.T) {
	out := "\"llama-server.exe\",\"125756\",\"Console\",\"1\",\"12,345 K\"\r\n"
	got, ok := parseExternalLlamaServerTasklist(out, 130001)
	if !ok {
		t.Fatal("expected external llama-server to be found")
	}
	if got.PID != 125756 {
		t.Fatalf("expected pid 125756, got %d", got.PID)
	}
	if got.Command != "llama-server.exe" {
		t.Fatalf("expected command llama-server.exe, got %q", got.Command)
	}
}

func TestParseExternalLlamaServerTasklist_IgnoresSelfPID(t *testing.T) {
	out := "\"llama-server.exe\",\"130001\",\"Console\",\"1\",\"12,345 K\"\r\n"
	_, ok := parseExternalLlamaServerTasklist(out, 130001)
	if ok {
		t.Fatal("expected self pid process to be ignored")
	}
}

func TestParseExternalLlamaServerTasklist_NoMatch(t *testing.T) {
	out := "INFO: No tasks are running which match the specified criteria.\r\n"
	_, ok := parseExternalLlamaServerTasklist(out, 130001)
	if ok {
		t.Fatal("expected no external llama-server match")
	}
}
