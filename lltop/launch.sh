#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
configuration="${LLTOP_CONFIGURATION:-Release}"

dotnet run --project "$script_dir/lltop.csproj" --configuration "$configuration" --no-build -- "$@"
