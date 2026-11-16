#!/usr/bin/env bash
# Signs a release's list of programs: writes SHA256SUMS.programs.sig next to SHA256SUMS.programs in
# a folder of release archives. PCs and stations check that signature themselves before installing
# a build their server offers (ReleaseSignature in src/TapQueue.Shared), so a signed release
# installs without them reaching GitHub. See docs/releasing.md, "Signed releases".
#
#   scripts/sign-release.sh dist
#
# The key (scripts/new-release-key.sh makes one) is taken from, in order:
#   TAPQUEUE_RELEASE_KEY        the private key itself, PEM (how the release workflow passes its secret)
#   TAPQUEUE_RELEASE_KEY_FILE   a file holding it
#   ~/.config/tapqueue/release-key.pem
# Without a key the release is left unsigned, with a warning: PCs and stations then check its builds
# against the release on GitHub instead. TAPQUEUE_REQUIRE_SIGNING=1 makes that an error once
# release-keys.txt names a key, so a release from a repository that signs can't go out unsigned.
set -euo pipefail

dir=${1:?Usage: $0 <folder of release archives>}
list=$dir/SHA256SUMS.programs
keys=$(cd "$(dirname "$0")/.." && pwd)/src/TapQueue.Shared/release-keys.txt
public_keys=$(grep -v '^[[:space:]]*#' "$keys" | grep -v '^[[:space:]]*$' || true)

if [ ! -s "$list" ]; then
    echo "sign-release: $list isn't there; run scripts/program-sums.sh $dir first." >&2
    exit 1
fi

temp=$(mktemp -d)
trap 'rm -rf "$temp"' EXIT
if [ -n "${TAPQUEUE_RELEASE_KEY:-}" ]; then
    (umask 077 && printf '%s\n' "$TAPQUEUE_RELEASE_KEY" > "$temp/key.pem")
    key=$temp/key.pem
else
    key=${TAPQUEUE_RELEASE_KEY_FILE:-$HOME/.config/tapqueue/release-key.pem}
fi

if [ ! -s "$key" ]; then
    rm -f "$list.sig"
    if [ "${TAPQUEUE_REQUIRE_SIGNING:-}" = 1 ] && [ -n "$public_keys" ]; then
        echo "sign-release: no release key (TAPQUEUE_RELEASE_KEY or $key), and release-keys.txt says releases are signed." >&2
        exit 1
    fi
    echo "sign-release: no release key, so this release isn't signed; PCs and stations will check its builds against GitHub." >&2
    exit 0
fi

openssl dgst -sha256 -sign "$key" -out "$temp/sig.der" "$list"
base64 < "$temp/sig.der" | tr -d '\n' > "$list.sig"
echo >> "$list.sig"

# A signature nothing trusts is worse than none: check it against the keys the programs are built with.
verified=
while IFS= read -r public; do
    [ -n "$public" ] || continue
    printf '%s\n' "$public" | base64 -d > "$temp/public.der" 2>/dev/null || continue
    if openssl dgst -sha256 -verify "$temp/public.der" -keyform DER -signature "$temp/sig.der" "$list" >/dev/null 2>&1; then
        verified=1
        break
    fi
done <<< "$public_keys"
if [ -z "$verified" ]; then
    rm -f "$list.sig"
    echo "sign-release: the release key isn't one of the keys in $keys, so nothing would trust this signature." >&2
    echo "              Run scripts/new-release-key.sh with this key to add its public half, and commit that file." >&2
    exit 1
fi
echo "sign-release: signed $(basename "$list")"
