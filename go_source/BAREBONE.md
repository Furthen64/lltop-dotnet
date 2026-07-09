# Barebone Code Files

## Entry Point
- `cmd/lltop/main.go`

## Module
- `go.mod`
- `go.sum`

## Internal Packages

### config
- `internal/config/config.go`
- `internal/config/config_load_test.go`
- `internal/config/first_run_wizard.go`
- `internal/config/first_run_wizard_test.go`
- `internal/config/first_run_wizard_tui.go`
- `internal/config/profile.go`
- `internal/config/validate.go`

### history
- `internal/history/failure_match.go`
- `internal/history/profile_summary.go`
- `internal/history/profile_summary_test.go`
- `internal/history/run_record.go`
- `internal/history/store.go`

### parser
- `internal/parser/hint_rules.json`
- `internal/parser/llama_logs.go`
- `internal/parser/llama_logs_test.go`

### runner
- `internal/runner/command.go`
- `internal/runner/command_test.go`
- `internal/runner/runner.go`
- `internal/runner/signals.go`

### ui
- `internal/ui/external_log.go`
- `internal/ui/external_log_test.go`
- `internal/ui/external_process.go`
- `internal/ui/external_process_test.go`
- `internal/ui/model.go`
- `internal/ui/styles.go`
- `internal/ui/views.go`
- `internal/ui/views_test.go`
