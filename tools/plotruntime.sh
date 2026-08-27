#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
python="$script_dir/.venv/bin/python3"
runs_dir="${XDG_CONFIG_HOME:-$HOME/.config}/lltop/runs"

if [[ ! -x "$python" ]]; then
    echo "Python virtual environment not found: $python" >&2
    exit 1
fi

if [[ ! -d "$runs_dir" ]]; then
    echo "lltop runs directory not found: $runs_dir" >&2
    exit 1
fi

latest=$(find "$runs_dir" -maxdepth 1 -type f -name 'run-*.dat' -printf '%T@ %p\n' | sort -nr | head -n 1 | cut -d' ' -f2-)
if [[ -z "$latest" ]]; then
    echo "No run-*.dat files found in $runs_dir" >&2
    exit 1
fi

exec "$python" "$script_dir/realtime_graph.py" "$latest" --follow "$@"
