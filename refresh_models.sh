#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--help" ]]; then
    cat <<'EOF'
Usage: ./refresh_models.sh [--db PATH] [--limit COUNT]

Prompts for Hugging Face, Ollama Library, or both, then refreshes the local
SQLite catalog. The options are passed to the catalog importer.
EOF
    exit 0
fi

cat <<'EOF'
Refresh the local lltop model catalog.

Choose a source:
  1) Hugging Face
  2) Ollama Library
  3) ModelScope
  4) All three
EOF

read -r -p "Source [1-4]: " choice
case "$choice" in
    1) source="huggingface" ;;
    2) source="ollama" ;;
    3) source="modelscope" ;;
    4) source="all" ;;
    *) echo "Please choose 1, 2, 3, or 4." >&2; exit 2 ;;
esac

exec python3 "$script_dir/tools/refresh_model_catalog.py" --source "$source" "$@"
