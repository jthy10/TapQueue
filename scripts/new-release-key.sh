#!/usr/bin/env bash
# Makes the key that client and station builds are signed with (ECDSA P-256), and prints its public
# half the way the programs carry it. See docs/releasing.md, "The release key".
#
#   scripts/new-release-key.sh [private-key-file]     default: ~/.config/tapqueue/release-key.pem
#
# The private key signs every build PCs and stations will install by themselves. Keep it off the
# TapQueue server: the point of signing is that a server can only hand out builds you signed.
set -euo pipefail

key=${1:-$HOME/.config/tapqueue/release-key.pem}
if [ -e "$key" ]; then
    echo "$key already exists; its public key is:" >&2
else
    mkdir -p "$(dirname "$key")"
    (umask 077 && openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$key")
    echo "Wrote the private key to $key. Its public key is:" >&2
fi
openssl pkey -in "$key" -pubout -outform DER | base64 -w0
echo
