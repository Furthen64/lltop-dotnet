# lltop-dotnet

A .NET 10 + Terminal.Gui v2 control center for llama.cpp's `llama-server`.

## Current features

- Create, edit, duplicate, delete, validate, and reload TOML profiles in the TUI.
- Start a selected profile with argument-safe process invocation.
- Gracefully stop `llama-server` with `SIGINT` on Unix, escalating to a process-tree kill after a timeout.
- Force-kill a managed server when needed.
- Display live stdout/stderr, PID, uptime, bind address, model, and launch state.
- Parse and display throughput, progress, GPU offload, memory/context data, errors, and hints.
- Persist a timestamped log for each run.
- Persist JSON run history with per-profile performance summaries, sparklines, and editable notes.
- Warn before repeating a recently failed startup configuration.
- Detect externally started `llama-server` processes and follow their logs when available.
- Copy launch commands, toggle log autoscroll, and inspect history from the keyboard.
- Configure the llama.cpp binary and model directory with a first-run wizard.
- Scan `.gguf` and `.bin` models after setup and generate profiles with Qwen, GPT-OSS, DeepSeek, or safe generic defaults.
- Enable Qwen3.6-35B-A3B and Qwen3.8-27B vision profiles with a matching `mmproj-BF16.gguf` projector.
- Discover sibling `mmproj*.gguf` files and use their GGUF metadata to suggest the matching vision projector.
- Exclude local models from discovery with glob patterns in `<models_dir>/.llmignore`.
- Run baseline-plus-sweep memory benchmarks with warmup, post-warmup VRAM sampling,
  OOM stop/continue handling, and standalone HTML/JSON reports.

Run with:

```sh
./checkreqs.sh
./lltop/build.sh
./launch.sh
```

`checkreqs.sh` verifies that Ubuntu has the .NET 10 SDK needed to build the app
and prints installation instructions when it is missing.

The main keys are shown in the application footer. Press `d` to duplicate the
selected profile, `H` for run history and notes, `c` to copy the launch command,
and `l` to toggle log autoscroll. Profiles
are stored under `~/.config/lltop/profiles` and run records under
`~/.config/lltop/runs` by default.

## Benchmark sweeps

Select a profile and press `b` to configure a benchmark. The server must be
idle: lltop refuses to start a benchmark while a managed or externally detected
`llama-server` is active. Enter one sweep per line as either a numeric range,
such as `ctx=4096:8192`, or categorical values, such as
`cache_k=q4_0,q8_0`. Each benchmark runs the baseline followed by independent
one-at-a-time sweep cases; it never combines parameter changes.

The configured prompt is the global warmup workload for every case. lltop waits
up to 300 seconds for readiness, then samples available VRAM once per second for
10 seconds after warmup. Press `B` to cancel; lltop stops the benchmark-owned
server and records cancellation. OOM outcomes can stop the remaining cases or
continue, as chosen in setup. Benchmark executions are not added to normal run
history. JSON and self-contained HTML reports are saved in `benchmarks_dir`
(`~/.config/lltop/benchmarks` by default).

## Theme

lltop ships with the `midnight` (default) and `nord` themes. Press `t` in the
main window to cycle between them; the choice is saved to configuration. You can
also set `theme = "midnight"` or `theme = "nord"` in
`~/.config/lltop/config.toml`. Themes use semantic
tokens for selection, hotkeys, warnings, errors, and memory-fit states, so
future palettes can change the appearance without changing the meaning of
`FULLY ON GPU`, `TIGHT`, or `PARTIAL OFFLOAD REQUIRED`.

Model discovery reads an optional `.llmignore` from the configured models directory.
Blank lines and `#` comments are ignored; `*`, `?`, `**`, directory patterns, and
later `!` negation rules are supported. For example:

```gitignore
archive/
experiments/**/*.gguf
mmproj*.gguf
!vision/mmproj-BF16.gguf
```

Verify changes with:

```sh
dotnet build lltop/lltop.csproj --no-restore
dotnet test tests/lltop.Tests/lltop.Tests.csproj --no-restore
```

## Model catalog refresh

`./refresh_models.sh` builds a small local SQLite catalog from Hugging Face,
the Ollama Library, ModelScope, or all three. It asks which source to use and
stores its cache under `~/.local/share/lltop/model-catalog.sqlite` by default
(override with `XDG_DATA_HOME` or pass `--db PATH`). The catalog is optional
and is not used to launch models yet.
