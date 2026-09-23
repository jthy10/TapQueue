#!/usr/bin/env bash
# Downloads a TapQueue server or station release from GitHub and installs it.
#
#   curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s server
#   curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s station
#
# Installs the newest release, or a given one: ... | sudo bash -s station 0.3.0
# Run it again to upgrade. The archive's own install-server.sh / install-station.sh does the work.
set -euo pipefail

repo=jthy10/TapQueue
component=${1:-}
version=${2:-}

case "$component" in
    server | station) ;;
    *)
        echo "Usage: install.sh server|station [version]" >&2
        exit 2
        ;;
esac
if [ "$(id -u)" -ne 0 ]; then
    echo "Run this as root (curl ... | sudo bash -s $component)." >&2
    exit 1
fi
if [ "$(uname -m)" != x86_64 ]; then
    echo "TapQueue $component releases are built for x86_64 Linux; this machine is $(uname -m)." >&2
    exit 1
fi
for tool in curl tar sha256sum; do
    command -v "$tool" >/dev/null || { echo "Needs $tool (sudo apt install $tool)." >&2; exit 1; }
done

# Server and station releases are tagged server-vX.Y.Z (the Windows client has its own client-v tags).
if [ -z "$version" ]; then
    version=$(curl -fsSL "https://api.github.com/repos/$repo/releases?per_page=100" |
        grep -o '"tag_name": *"server-v[^"]*"' | head -1 | sed 's/.*"server-v\([^"]*\)"/\1/')
    if [ -z "$version" ]; then
        echo "Couldn't find a TapQueue server release on https://github.com/$repo/releases" >&2
        exit 1
    fi
fi

name="TapQueue_${component}_${version}_linux-x64"
base="https://github.com/$repo/releases/download/server-v$version"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "Downloading TapQueue $component $version"
curl -fL --progress-bar -o "$tmp/$name.tar.gz" "$base/$name.tar.gz"
curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS"
(cd "$tmp" && grep " $name.tar.gz\$" SHA256SUMS | sha256sum --check --quiet -)

tar -xzf "$tmp/$name.tar.gz" -C "$tmp"
"$tmp/$name/install-$component.sh"
