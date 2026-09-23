#!/usr/bin/env bash
# Prints the section for a version from a Keep-a-Changelog file: release-notes.sh CHANGELOG.md 0.3.0
set -euo pipefail
file=$1
version=$2
awk -v v="$version" '
    $0 ~ "^## \\[" v "\\]" { found = 1; next }
    found && /^## \[/ { exit }
    found { print }
' "$file" > /tmp/release-notes.$$
if ! grep -q '[^[:space:]]' /tmp/release-notes.$$; then
    echo "::error::$file has no section for [$version]" >&2
    rm -f /tmp/release-notes.$$
    exit 1
fi
cat /tmp/release-notes.$$
rm -f /tmp/release-notes.$$
