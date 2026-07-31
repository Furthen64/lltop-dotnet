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
- Enable Qwen3.6-35B-A3B vision profiles with a matching `mmproj-BF16.gguf` projector.
- Discover sibling `mmproj*.gguf` files and use their GGUF metadata to suggest the matching vision projector.
- Exclude local models from discovery with glob patterns in `<models_dir>/.llmignore`.

Run with:

```sh
./checkreqs.sh
./lltop/build.sh
./launch.sh
```

`checkreqs.sh` verifies that Ubuntu has the .NET 10 SDK needed to build the app
and prints installation instructions when it is missing.

The main keys are shown in the application footer. Press `N` for run history and
notes, `c` to copy the launch command, and `l` to toggle log autoscroll. Profiles
are stored under `~/.config/lltop/profiles` and run records under
`~/.config/lltop/runs` by default.

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
