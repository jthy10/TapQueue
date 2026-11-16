#!/usr/bin/env bash
# Makes the key TapQueue's releases are signed with (ECDSA P-256), and adds its public half to
# src/TapQueue.Shared/release-keys.txt, which is built into every program. See docs/releasing.md,
# "Signed releases".
#
#   scripts/new-release-key.sh [private-key-file]     default: ~/.config/tapqueue/release-key.pem
#
# The private key decides what every PC and station installs by itself. Keep it out of the
# repository and off every TapQueue server: put it in the repository's TAPQUEUE_RELEASE_KEY secret
# (Settings -> Secrets and variables -> Actions) and somewhere safe offline, and nowhere else.
set -euo pipefail
cd "$(dirname "$0")/.."

key=${1:-$HOME/.config/tapqueue/release-key.pem}
keys=src/TapQueue.Shared/release-keys.txt

if [ -e "$key" ]; then
    echo "$key already exists; using it." >&2
else
    mkdir -p "$(dirname "$key")"
    (umask 077 && openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$key")
    echo "Wrote the private key to $key." >&2
fi

public=$(openssl pkey -in "$key" -pubout -outform DER | base64 | tr -d '\n')
if grep -qxF "$public" "$keys"; then
    echo "Its public key is already in $keys." >&2
else
    printf '%s\n' "$public" >> "$keys"
    echo "Added its public key to $keys. Commit that file: programs built from now on trust releases signed with this key." >&2
fi

cat >&2 <<NEXT

Next:
  1. Put the private key in the repository's secrets, as TAPQUEUE_RELEASE_KEY:
       gh secret set TAPQUEUE_RELEASE_KEY < "$key"
  2. Commit $keys and cut a release. From that release on, PCs and stations
     running it install signed builds without asking GitHub.
  3. Keep a copy of $key somewhere safe and offline. Whoever has it decides what every
     PC and station runs.
NEXT
echo "$public"
