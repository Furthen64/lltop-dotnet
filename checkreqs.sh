#!/usr/bin/env bash
set -euo pipefail

readonly required_sdk="10.0"
readonly install_docs="https://learn.microsoft.com/dotnet/core/install/linux-ubuntu-install"

print_install_help() {
    local ubuntu_version="$1"

    printf '\nlltop requires the .NET %s SDK (the runtime alone is not enough).\n\n' "$required_sdk"

    if [[ "$ubuntu_version" == "22.04" ]]; then
        cat <<EOF
On Ubuntu 22.04, enable Canonical's .NET backports repository, then install the SDK:

  sudo add-apt-repository ppa:dotnet/backports
  sudo apt-get update
  sudo apt-get install -y dotnet-sdk-${required_sdk}
EOF
    else
        cat <<EOF
Install the SDK from Ubuntu's package repository:

  sudo apt-get update
  sudo apt-get install -y dotnet-sdk-${required_sdk}
EOF
    fi

    cat <<EOF

Then run this check again:

  ./checkreqs.sh

Official Ubuntu installation guide:
  ${install_docs}
EOF
}

if [[ ! -r /etc/os-release ]]; then
    echo "Error: cannot identify this operating system (/etc/os-release is missing)." >&2
    echo "This project supports this setup check on Ubuntu only." >&2
    exit 2
fi

os_id="$(sed -n 's/^ID=//p' /etc/os-release | tr -d '"' | head -n 1)"
ubuntu_version="$(sed -n 's/^VERSION_ID=//p' /etc/os-release | tr -d '"' | head -n 1)"

if [[ "$os_id" != "ubuntu" ]]; then
    echo "Error: this setup check supports Ubuntu only (detected: ${os_id:-unknown})." >&2
    exit 2
fi

echo "Checking build requirements for lltop on Ubuntu ${ubuntu_version:-unknown}..."

if ! command -v dotnet >/dev/null 2>&1; then
    echo "Not ready: the dotnet command was not found."
    print_install_help "$ubuntu_version"
    exit 1
fi

installed_sdks="$(dotnet --list-sdks 2>/dev/null || true)"
if ! grep -Eq "^${required_sdk//./\\.}\." <<<"$installed_sdks"; then
    echo "Not ready: .NET SDK ${required_sdk} is not installed."

    if [[ -n "$installed_sdks" ]]; then
        echo "Installed SDKs:"
        sed 's/^/  /' <<<"$installed_sdks"
    else
        echo "No .NET SDKs were reported. A runtime-only installation cannot build the app."
    fi

    print_install_help "$ubuntu_version"
    exit 1
fi

selected_sdk="$(dotnet --version 2>/dev/null || true)"
echo "Ready: .NET SDK ${required_sdk} is installed."
[[ -n "$selected_sdk" ]] && echo "dotnet currently selects SDK $selected_sdk."
echo "Build with: ./lltop/build.sh"
