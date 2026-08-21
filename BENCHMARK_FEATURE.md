# Benchmark Sweep Feature

This document is the implementation checklist and working source of truth for benchmark sweeps. Update a task to `DONE` only after its completion checks have passed; otherwise leave it `WIP`.

## Goal and acceptance criteria

Provide a benchmark workflow that starts from a baseline profile and runs one-at-a-time parameter sweeps. Each selected setting must be tested at its minimum, middle, and maximum values while all other settings remain at the baseline. Numeric settings and categorical settings that affect memory must be supported.

The benchmark owns its server processes and must not run while another server is active. Workload configuration is global to the benchmark, rather than per-sweep case. After chat warmup, record post-warmup VRAM usage and present progress and results in the TUI. Save standalone, self-contained HTML and JSON reports under `benchmarks_dir`.

Defaults and constraints:

- Readiness timeout: 300 seconds.
- VRAM sampling interval: 1 second.
- Post-warmup settling window: 10 seconds.
- Each case is run independently; sweeps do not vary multiple settings at once.
- Benchmark activity must be isolated from ordinary run history.

## Status legend

- `WIP` — not implemented or actively in progress.
- `DONE` — implemented and verified by its listed tests.

## 1. Benchmark data model and persistence — WIP

Define persisted benchmark, sweep-case, workload, measurement, status, error, and report metadata models. Include baseline inputs, case ordering, timestamps, telemetry availability, OOM outcome, and links/names for produced artifacts.

Completion checks:

- JSON round-trips preserve completed, failed, cancelled, and OOM cases.
- Schema/version handling has tests for missing or older optional fields.
- Benchmark records are separate from ordinary run-history records.

## 2. Config additions and benchmark artifact directory — WIP

Add `benchmarks_dir` to application configuration, expand it like the existing directory settings, create it on load, and persist it when saving configuration.

Completion checks:

- Default configuration provides a usable benchmark directory.
- Custom and `~`-based paths expand correctly and are created.
- Saving then loading preserves `benchmarks_dir`.

## 3. Sweep-case generation and validation — WIP

Create validation and deterministic case generation for a baseline plus one-at-a-time min/mid/max sweeps. Support numeric ranges and categorical memory settings, reject invalid ranges/options, deduplicate equal values, and make the generated case labels and order stable.

Completion checks:

- Numeric generation covers baseline/min/mid/max without changing unrelated settings.
- Categorical generation covers the permitted values without invalid mixes.
- Validation explains unavailable, duplicate, and invalid sweep choices.

## 4. Server lifecycle ownership and ordinary-history isolation — WIP

Implement a benchmark runner that exclusively owns each server lifecycle, refuses to start if any managed or external server is active, and cleans up its own process before continuing or exiting. Do not add benchmark launches to ordinary run history.

Completion checks:

- An active managed or externally detected server prevents benchmark start.
- Each case starts and stops exactly one owned server process.
- Benchmark executions do not appear in normal history views or files.

## 5. Readiness polling, chat warmup, and cancellation — WIP

Poll the server readiness endpoint for up to 300 seconds, execute the configured global chat workload after readiness, and support cancellation throughout setup, startup, polling, warmup, settling, and teardown.

Completion checks:

- Delayed readiness succeeds before the timeout; timeout records a useful error.
- Chat warmup is issued only after readiness and uses the global workload.
- Cancellation stops the owned process and persists a cancelled outcome.

## 6. VRAM sampling, OOM detection, and stop/continue policy — WIP

Sample available VRAM telemetry once per second, retaining post-warmup samples over the 10-second settling window. Detect OOM conditions from process/server signals, record unavailable telemetry explicitly, and honor the selected policy to stop the sweep or continue with later cases after OOM.

Completion checks:

- Measurements exclude pre-warmup data and summarize the settling-window data.
- Missing telemetry yields a completed result marked unavailable, not a false zero measurement.
- OOM is recorded and verified for both stop and continue policies.

## 7. Benchmark setup, progress, and results TUI — WIP

Add TUI flows to configure a benchmark, choose settings and ranges/categories, set workload and OOM policy, review generated cases, observe live progress, and inspect completed results including errors and telemetry availability.

Completion checks:

- Keyboard navigation and focus work without a mouse.
- Setup validation blocks invalid benchmarks with actionable messages.
- Progress and final results distinguish success, failure, cancellation, OOM, and unavailable telemetry.

## 8. Self-contained HTML report generation — WIP

Generate a portable HTML report with embedded styles/scripts/data plus a JSON report for every benchmark. Reports must include configuration, workload, case-level outcomes, post-warmup VRAM measurements, errors, timestamps, and artifact references.

Completion checks:

- HTML and JSON reports are written beneath `benchmarks_dir`.
- HTML opens without network access and renders baseline and sweep comparisons.
- Report escaping safely handles profile names, prompts, and error text.

## 9. Automated tests — WIP

Add focused unit and integration tests for persistence, configuration, case generation, server ownership, readiness/warmup/cancellation, telemetry/OOM handling, and report generation. Include TUI coverage where the test harness permits it.

Completion checks:

- New tests cover all feature-level acceptance criteria.
- The complete test suite passes.

## 10. README and user-facing keyboard/help documentation — WIP

Document benchmark setup, required idle-server state, workload behavior, defaults, OOM policy, report location, and all relevant keyboard shortcuts in the README and in-app help/footer text.

Completion checks:

- Documentation matches implemented controls and defaults.
- Help describes cancellation and where reports are saved.

## Final verification checklist

- [ ] Build succeeds.
- [ ] Full test suite passes.
- [ ] A successful baseline-plus-sweep benchmark completes.
- [ ] OOM behavior works in both stop and continue modes.
- [ ] Unavailable telemetry is reported accurately.
- [ ] Cancellation cleans up the benchmark server and persists the result.
- [ ] Generated HTML reports render standalone.

