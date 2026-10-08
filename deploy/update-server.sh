#!/usr/bin/env bash
# Upgrades tapqueue-server to a release an admin picked in the console (Server > Check for updates).
#
# tapqueue-server runs as the unprivileged tapqueue user and can't replace its own programs, so it
# asks for an upgrade by writing the version it wants to /var/lib/tapqueue/update-request.
# tapqueue-update.path notices the file and starts tapqueue-update.service, which runs this as root.
#
# The request only names a version. Everything else is fixed here: the release comes from this
# repository's GitHub releases, its SHA256SUMS must match, it must be newer than what's installed,
# and the archive's own install-server.sh does the install, as `install.sh server` would.
# Progress and the outcome go to /var/lib/tapqueue-update/status, which the console shows.
#
# Run it by hand to see what it would do: sudo /opt/tapqueue/update-server.sh 0.8.0
set -euo pipefail

repo=jthy10/TapQueue
prefix=/opt/tapqueue
request=/var/lib/tapqueue/update-request
statedir=/var/lib/tapqueue-update
status=$statedir/status
tmp=""

# Everything is in a function so bash has read the whole file before install-server.sh replaces it.
main() {
    if [ "$(id -u)" -ne 0 ]; then
        echo "Run this as root." >&2
        exit 1
    fi
    install -d -m 755 "$statedir"

    local version=${1:-}
    if [ -z "$version" ]; then
        version=$(take_request) || exit 0
    fi
    if ! [[ $version =~ ^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$ ]]; then
        fail "" "The update request didn't name a version like 1.2.3."
    fi

    local installed
    installed=$(server_version "$prefix/tapqueue-server")
    if [ -n "$installed" ] && ! newer "$version" "$installed"; then
        fail "$version" "$installed is installed already; updates only go to a newer version."
    fi

    report running "$version" "Downloading TapQueue server $version"
    for tool in curl tar sha256sum; do
        command -v "$tool" >/dev/null || fail "$version" "Needs $tool (apt install $tool)."
    done

    local name="TapQueue_server_${version}_linux-x64"
    local base="https://github.com/$repo/releases/download/server-v$version"
    tmp=$(mktemp -d)
    trap '[ -z "$tmp" ] || rm -rf "$tmp"' EXIT

    curl -fsSL --proto '=https' --max-time 600 -o "$tmp/$name.tar.gz" "$base/$name.tar.gz" ||
        fail "$version" "Couldn't download $name.tar.gz from GitHub."
    curl -fsSL --proto '=https' --max-time 60 -o "$tmp/SHA256SUMS" "$base/SHA256SUMS" ||
        fail "$version" "Couldn't download SHA256SUMS from GitHub."
    grep -q " $name.tar.gz\$" "$tmp/SHA256SUMS" ||
        fail "$version" "SHA256SUMS doesn't list $name.tar.gz."
    (cd "$tmp" && grep " $name.tar.gz\$" SHA256SUMS | sha256sum --check --quiet --strict -) ||
        fail "$version" "$name.tar.gz doesn't match its SHA-256 in SHA256SUMS. Nothing was installed."

    report running "$version" "Installing TapQueue server $version"
    tar -xzf "$tmp/$name.tar.gz" -C "$tmp" --no-same-owner ||
        fail "$version" "Couldn't unpack $name.tar.gz."
    local unpacked
    unpacked=$(server_version "$tmp/$name/tapqueue-server")
    [ "$unpacked" = "$version" ] ||
        fail "$version" "The archive's tapqueue-server says it's ${unpacked:-unknown}, not $version. Nothing was installed."

    if ! "$tmp/$name/install-server.sh"; then
        fail "$version" "install-server.sh failed. See: journalctl -u tapqueue-update -n 100"
    fi
    report done "$version" "Updated to TapQueue server $version"
}

# Reads and removes the request the server wrote. Removing it first means a failed update isn't
# retried over and over by the path unit.
take_request() {
    [ -e "$request" ] || [ -L "$request" ] || return 1
    local line=""
    # The data directory belongs to the tapqueue user: never follow a link it put there.
    if [ -f "$request" ] && [ ! -L "$request" ]; then
        line=$(head -c 64 "$request" | head -n 1 | tr -d '[:space:]')
    fi
    rm -f "$request"
    echo "$line"
}

# The X.Y.Z out of `tapqueue-server --version` ("tapqueue-server 0.8.0+1a2b3c4"), or nothing.
server_version() {
    [ -x "$1" ] || return 0
    { timeout 5 "$1" --version 2>/dev/null || true; } | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -n 1 || true
}

# newer A B: true if version A is newer than B (both X.Y.Z).
newer() {
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]
}

# report state version message: replaces the status file in one go so the server never reads half of it.
report() {
    local tmpstatus
    tmpstatus=$(mktemp "$statedir/.status.XXXXXX")
    printf 'state=%s\nversion=%s\nat=%s\nmessage=%s\n' "$1" "$2" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$3" > "$tmpstatus"
    chmod 644 "$tmpstatus"
    mv -f "$tmpstatus" "$status"
    echo "$3"
}

fail() {
    report failed "$1" "$2" >&2
    exit 1
}

main "$@"
exit
