#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
configuration="${LLTOP_CONFIGURATION:-Release}"
artifact="$script_dir/lltop/bin/$configuration/net10.0/lltop.dll"

needs_build=false
if [[ ! -f "$artifact" ]]; then
    needs_build=true
elif find "$script_dir/lltop" -type f \( -name '*.cs' -o -name '*.csproj' \) -newer "$artifact" -print -quit | grep -q .; then
    needs_build=true
fi

if [[ "$needs_build" == true ]]; then
    echo "lltop sources changed; building $configuration..."
    "$script_dir/build.sh" "$configuration"
fi

dotnet run --project "$script_dir/lltop/lltop.csproj" --configuration "$configuration" --no-build -- "$@"
