# Releasing

TapQueue has two release tracks, each with its own version, changelog and tag:

| Track | Version in `Directory.Build.props` | Changelog | Tag |
|---|---|---|---|
| Server, station and `tapqueue-admin` | `<Version>` | `CHANGELOG.md` | `server-vX.Y.Z` |
| Windows and Linux clients | `<TapQueueClientVersion>` | `CHANGELOG-client.md` | `client-vX.Y.Z` |

From 0.17 the two versions are kept the same and released together, so one number says what a
site runs; a fix to one side alone still bumps both.

Both use [Semantic Versioning](https://semver.org/). Before 1.0, bump the minor version
(0.**x**.0) for anything that breaks an existing install (config, API, database), and the patch
version for fixes. The server has to keep working with older clients, and the other way round,
so client API changes are additive (`/api/v1`).

Every build knows the commit it came from: `tapqueue-server --version` prints e.g. `0.2.0+1a2b3c4`.

## Day to day

Add a line under `## [Unreleased]` in the right changelog with each user-visible change: what
changed and, if it breaks something, what to do about it.

## Cutting a release

1. Set the version in `Directory.Build.props` (`<Version>` or `<TapQueueClientVersion>`).
2. In the changelog, rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`, add a fresh empty
   `## [Unreleased]` above it, and update the compare links at the bottom.
3. Commit ("Release server X.Y.Z" / "Release client X.Y.Z"), then tag and push:
   ```sh
   git tag -a server-vX.Y.Z -m "TapQueue server X.Y.Z"      # or client-vX.Y.Z
   git push origin main server-vX.Y.Z
   ```
4. The `release` workflow checks that the tag matches the version, builds (server: on Linux,
   with the tests; client: the Windows installer on Windows and the Linux tarball on Linux), and
   publishes a GitHub release whose
   notes are that version's section of the changelog.
5. Set the version to the next expected one so development builds don't claim to be the release.

For a client release, push it to your PCs afterwards: **Updates → Check for updates → Publish** in
the console, or `sudo tapqueue-admin clients update` on the server. After a server release and
upgrade, the same under Release stations (`tapqueue-admin stations update`). Both look for the
asset names below, so keep them as they are.

## Signed releases

PCs and stations install new builds from their TapQueue server by themselves, as SYSTEM or root,
so a server must only be able to pass TapQueue's own releases on. Each release's list of its
programs (`SHA256SUMS.programs`) is signed, and every PC and station checks that signature
itself against the public keys it was built with, with no need to reach GitHub.

**Once**, to start signing:

```sh
scripts/new-release-key.sh                                  # makes ~/.config/tapqueue/release-key.pem
gh secret set TAPQUEUE_RELEASE_KEY < ~/.config/tapqueue/release-key.pem
git add src/TapQueue.Shared/release-keys.txt && git commit -m "Release key"
```

The script makes an ECDSA P-256 key and adds its public half to
`src/TapQueue.Shared/release-keys.txt`, which is built into every program. The private half goes
in the repository's `TAPQUEUE_RELEASE_KEY` secret and somewhere safe offline, and nowhere else,
least of all on a TapQueue server.

From then on:

- `scripts/package.sh` and the `release` workflow run `scripts/sign-release.sh dist`, which writes
  `SHA256SUMS.programs.sig` (the signature, in base64) next to the list. It checks the signature
  against `release-keys.txt` before leaving it there, so a release can't go out signed with a key
  nothing trusts.
- With a key in `release-keys.txt`, the workflow fails a release it can't sign.
- The server downloads the list and its signature with a release's builds and hands them out; the
  console's **Updates** page shows *Signature* for a build PCs will install without asking GitHub.
- Programs built **before** the key was added don't know it. They carry on checking against
  GitHub (below) until they've updated to a release built after it.

**Replacing the key**: run the script with a new file name, which adds a second public key. Ship
a release (signed with the old key) so everything learns the new one, move the secret to the new
key, and take the old line out of `release-keys.txt` a release later.

Whoever holds the private key decides what every site's PCs will run. Keep it accordingly.

A site that builds TapQueue itself signs its releases the same way with its own key, and gives
its PCs and stations the public half as `update_key` in `client.toml` and `station.toml`.

## What PCs and stations check

A build that comes without a signature (a release made without a key, or a PC or station older
than 0.19) is checked the older way. Each PC and station checks what it's offered against the release
itself: it downloads `SHA256SUMS.programs` for that version from GitHub
(`…/releases/download/client-vX.Y.Z/` for a PC, `server-vX.Y.Z/` for a station) and installs the
build only if its SHA-256 is listed there under its program's name.

- `scripts/program-sums.sh dist` writes that file from the archives in `dist/`; `scripts/package.sh`
  and the `release` workflow run it. **A release without it can't be installed from**, so keep it
  attached, and don't replace a release's archives without replacing it too.
- Whoever can publish releases in the repository decides what every site's PCs will run. Protect
  the GitHub account accordingly (two-factor sign-in, no spare tokens with release rights).
- A site that builds TapQueue itself publishes its releases the same way (same tags, same file)
  anywhere that serves HTTPS, and sets `releases_url` in `client.toml` and `station.toml` to the
  address the tags' folders are under.

## What a release contains

Server and station (`server-vX.Y.Z`):

| File | Contents |
|---|---|
| `TapQueue_server_X.Y.Z_linux-x64.tar.gz` | `tapqueue-server`, `tapqueue-admin`, `libe_sqlite3.so`, example config, systemd units, `install-server.sh`, `update-server.sh` |
| `TapQueue_station_X.Y.Z_linux-x64.tar.gz` | `tapqueue-station`, example config, systemd unit, pcProx udev rule, `install-station.sh` |
| `SHA256SUMS.programs` | the checksum of `tapqueue-station` itself, which stations check before installing it |
| `SHA256SUMS.programs.sig` | that list's signature, when the release is signed |
| `SHA256SUMS` | checksums of the above |

`install.sh` in the repository root downloads the newest of these and runs the installer in it.
The console's **Check for updates** (Server page) looks for the newest `server-vX.Y.Z` release that
isn't a draft or pre-release and has the server archive and `SHA256SUMS` attached, so keep those
names as they are.

Clients (`client-vX.Y.Z`):

| File | Contents |
|---|---|
| `TapQueue_client_X.Y.Z.exe` | The Windows installer: what people download and run on a PC |
| `TapQueue_client_X.Y.Z_win-x64.zip` | `TapQueueClient.exe` and `version.txt`: what servers download and hand out to PCs |
| `TapQueue_client_X.Y.Z_linux-x64.tar.gz` | `tapqueue-client`, `version.txt`, example config, systemd unit, desktop entry, `install-client.sh`; installed by `install.sh client`, and what servers download and hand out to Linux PCs |
| `SHA256SUMS.programs` | the checksums of `TapQueueClient.exe` and `tapqueue-client` themselves, which PCs check before installing them |
| `SHA256SUMS.programs.sig` | that list's signature, when the release is signed |
| `SHA256SUMS` | checksums of the above |

Build the server and station archives locally with `scripts/package.sh server`, the client zip
and Linux tarball with `scripts/package.sh client` (into `dist/`).
The installer can only be built on Windows: `scripts/package-client-installer.ps1`. CI builds it
on every push; download it from the run's artifacts.
