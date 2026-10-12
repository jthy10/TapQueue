#!/usr/bin/env bash
# Signs client and station programs with the release key: writes <program>.sig next to each one,
# which is packaged with it and which PCs and stations check before installing a build from their
# server. See docs/releasing.md, "Signed updates".
#
#   scripts/sign-build.sh <program>...
#
# The key (scripts/new-release-key.sh makes one) is taken from, in order:
#   TAPQUEUE_RELEASE_KEY        the private key itself, PEM (how the release workflow passes its secret)
#   TAPQUEUE_RELEASE_KEY_FILE   a file holding it
#   ~/.config/tapqueue/release-key.pem
# Without a key the programs are left unsigned, with a warning; TAPQUEUE_REQUIRE_SIGNING=1 makes
# that an error instead, so a release can't go out unsigned.
set -euo pipefail

temp=
trap '[ -z "$temp" ] || rm -f "$temp"' EXIT
if [ -n "${TAPQUEUE_RELEASE_KEY:-}" ]; then
    temp=$(mktemp)
    printf '%s\n' "$TAPQUEUE_RELEASE_KEY" > "$temp"
    key=$temp
else
    key=${TAPQUEUE_RELEASE_KEY_FILE:-$HOME/.config/tapqueue/release-key.pem}
fi

if [ ! -s "$key" ]; then
    if [ "${TAPQUEUE_REQUIRE_SIGNING:-}" = 1 ]; then
        echo "sign-build: no release key (TAPQUEUE_RELEASE_KEY or $key), and this build has to be signed." >&2
        exit 1
    fi
    echo "sign-build: no release key, so these are unsigned and PCs and stations won't install them from a server: $*" >&2
    exit 0
fi

for program in "$@"; do
    openssl dgst -sha256 -sign "$key" -out "$program.sig" "$program"
done
