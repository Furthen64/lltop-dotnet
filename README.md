# lltop-dotnet

A .NET 10 + Terminal.Gui v2 control center for llama.cpp's `llama-server`.

## Current features

- Create, edit, duplicate, delete, validate, and reload TOML profiles in the TUI.
- Start a selected profile with argument-safe process invocation.
- Gracefully stop `llama-server` with `SIGINT` on Unix, escalating to a process-tree kill after a timeout.
- Force-kill a managed server when needed.
- Display live stdout/stderr, PID, uptime, bind address, model, and launch state.
- Persist a timestamped log for each run.
- Configure the llama.cpp binary and model directory with a first-run wizard.
- Scan `.gguf` and `.bin` models after setup and generate starter profiles with Qwen, GPT-OSS, DeepSeek, or safe generic defaults.

Run with:

```sh
./lltop/launch.sh
```

The main keys are shown in the application footer. Profiles are stored under
`~/.config/lltop/profiles` by default.

Verify changes with:

```sh
dotnet build lltop/lltop.csproj --no-restore
dotnet test tests/lltop.Tests/lltop.Tests.csproj --no-restore
```
