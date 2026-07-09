package ui

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestNormalizeLogPathCandidate(t *testing.T) {
	tests := []struct {
		name   string
		input  string
		want   string
		wantOK bool
	}{
		{name: "regular absolute path", input: "/tmp/llama.log", want: "/tmp/llama.log", wantOK: true},
		{name: "deleted suffix", input: "/tmp/llama.log (deleted)", want: "/tmp/llama.log", wantOK: true},
		{name: "pipe", input: "pipe:[12345]", wantOK: false},
		{name: "socket", input: "socket:[12345]", wantOK: false},
		{name: "relative", input: "logs/llama.log", wantOK: false},
		{name: "tty", input: "/dev/pts/3", wantOK: false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, ok := normalizeLogPathCandidate(tt.input)
			if ok != tt.wantOK {
				t.Fatalf("expected ok=%t, got %t", tt.wantOK, ok)
			}
			if got != tt.want {
				t.Fatalf("expected path %q, got %q", tt.want, got)
			}
		})
	}
}

func TestTailFileLines(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "server.log")
	content := "line1\nline2\nline3\n"
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}

	lines, offset, err := tailFileLines(path, 2)
	if err != nil {
		t.Fatal(err)
	}
	if want := int64(len(content)); offset != want {
		t.Fatalf("expected offset %d, got %d", want, offset)
	}
	if !reflect.DeepEqual(lines, []string{"line2", "line3"}) {
		t.Fatalf("unexpected lines: %#v", lines)
	}
}

func TestReadFileLinesFromOffset(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "server.log")
	initial := "line1\nline2\n"
	if err := os.WriteFile(path, []byte(initial), 0o644); err != nil {
		t.Fatal(err)
	}

	_, offset, err := tailFileLines(path, 200)
	if err != nil {
		t.Fatal(err)
	}

	appendData := "line3\nline4\n"
	f, err := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.WriteString(appendData); err != nil {
		f.Close()
		t.Fatal(err)
	}
	if err := f.Close(); err != nil {
		t.Fatal(err)
	}

	lines, newOffset, err := readFileLinesFromOffset(path, offset)
	if err != nil {
		t.Fatal(err)
	}
	if want := int64(len(initial) + len(appendData)); newOffset != want {
		t.Fatalf("expected offset %d, got %d", want, newOffset)
	}
	if !reflect.DeepEqual(lines, []string{"line3", "line4"}) {
		t.Fatalf("unexpected lines: %#v", lines)
	}
}
