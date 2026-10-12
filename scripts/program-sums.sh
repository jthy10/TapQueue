#!/usr/bin/env bash
# Writes SHA256SUMS.programs into a folder of release archives: the SHA-256 of each program PCs and
# stations install from their server (TapQueueClient.exe, tapqueue-client, tapqueue-station), taken
# out of the archives as they'll be published. PCs and stations fetch this file from the release
# themselves and only install a build their server offers if it's listed (ReleaseCheck in
# src/TapQueue.Shared), so it has to be attached to every release.
#
#   scripts/program-sums.sh dist
set -euo pipefail

dir=${1:?Usage: $0 <folder of release archives>}
out=$dir/SHA256SUMS.programs
: > "$out"

sum() { # program name, read from standard input
    printf '%s  %s\n' "$(sha256sum | cut -d' ' -f1)" "$1" >> "$out"
}

for archive in "$dir"/TapQueue_client_*_win-x64.zip; do
    [ -e "$archive" ] || continue
    unzip -p "$archive" TapQueueClient.exe | sum TapQueueClient.exe
done
for archive in "$dir"/TapQueue_client_*_linux-x64.tar.gz; do
    [ -e "$archive" ] || continue
    tar -xzOf "$archive" --wildcards '*/tapqueue-client' | sum tapqueue-client
done
for archive in "$dir"/TapQueue_station_*_linux-x64.tar.gz; do
    [ -e "$archive" ] || continue
    tar -xzOf "$archive" --wildcards '*/tapqueue-station' | sum tapqueue-station
done

if [ ! -s "$out" ]; then
    echo "program-sums: no client or station archive in $dir" >&2
    rm -f "$out"
    exit 1
fi
cat "$out"
