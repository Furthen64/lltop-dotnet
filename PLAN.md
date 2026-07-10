# lltop .NET Port — Implementation Plan

lltop is a Terminal UI for managing llama.cpp server profiles. The legacy
implementation is in `go_source`; the .NET implementation is in `lltop`.

## Target

- .NET 10
- Terminal.Gui v2 (`2.4.17`)

## Completed

### Phase 1 — Terminal UI Foundation

- Created the .NET 10 console application.
- Initialized Terminal.Gui v2 and added the main `lltop` window.
- Added profile, log, status, and help areas.
- Added keyboard handling for selection, launch, stop, kill, restart, and quit.

### Phase 2 — Configuration and Profiles

- Added configuration loading from `~/.config/lltop/config.toml`.
- Added default configuration values for profile and log directories.
- Added environment-variable and `~` path expansion.
- Added directory creation for profiles and logs.
- Added first-run setup detection and a guided Terminal.Gui wizard.
- Added wizard fields for the `llama-server` binary/app directory and models directory.
- Added suggested defaults of `~/llama/app` and `~/llama/models`.
- Added validation, path expansion, models-directory creation, and config persistence.
- Added TOML profile discovery from `*.toml` files.
- Added profile defaults for the main llama.cpp server options.
- Added sorting and display of loaded profiles.

### Phase 3 — Basic Process Runner and Logs

- Added `llama-server` process launching from the selected profile.
- Added validation for the server executable and model path.
- Added construction of llama.cpp command-line arguments.
- Added asynchronous stdout and stderr capture.
- Added persistent per-run log files under the configured logs directory.
- Added a bounded live log view retaining the most recent 500 lines.
- Added runner status transitions for running, stopping, stopped, and failed states.

## Remaining Work

### Phase 4 — Log Parser and Metrics

- Port the Go parser into a dedicated C# parser model.
- Parse prompt/eval timing, throughput, progress, memory, GPU, and context data.
- Detect llama.cpp errors, cancellation, and actionable hints.
- Display parsed metrics in the status panel.
- Add parser unit tests using representative llama.cpp log lines.

### Phase 5 — Configuration UX and Full UI (in progress)

- Completed structured profile creation, editing, duplication, deletion, and reload.
- Completed profile validation and visible per-file load errors.
- Completed a framed dashboard layout, scrollable live log, server PID/uptime, and command preview.
- Completed argument-safe process launch, graceful Unix stop with timeout escalation, and separate force-kill.
- Remaining: restart confirmation and clipboard support.

### Phase 6 — History and Polish

- Persist run records and profile summaries.
- Add run history and annotations/notes.
- Add recent-failure detection and startup-failure handling.
- Improve process cleanup and graceful signal handling across platforms.
- Add automated tests for configuration, command construction, runner behavior,
  and UI-independent application logic.

## Current Verification

The project builds successfully with:

```text
dotnet build --no-restore
```
