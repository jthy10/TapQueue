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

## Signed updates

PCs and stations install new builds from their TapQueue server by themselves, as SYSTEM or root.
So that a server can only hand out builds and not invent them, the client and station programs are
signed with a release key when they're packaged, and every PC and station checks the signature
before installing (clients and stations from 0.17). The server never has the private key.

- `scripts/new-release-key.sh` makes the key (ECDSA P-256) in `~/.config/tapqueue/release-key.pem`
  and prints its public half. Keep the private key somewhere that isn't the TapQueue server, and
  keep a copy: without it you can't release a build your PCs will take.
- The public half is in `ReleaseSignature.ReleaseKeys` (`src/TapQueue.Shared`), built into every
  client and station. To replace a key, add the new one there, release with the old key until
  every PC runs a build that knows both, then sign with the new one and drop the old.
- `scripts/sign-build.sh`, which the packaging scripts run, writes `<program>.sig` next to each
  program. It takes the key from `TAPQUEUE_RELEASE_KEY` (the PEM itself) or
  `TAPQUEUE_RELEASE_KEY_FILE`, or the file above. Without a key the build is unsigned and it says so.
- The `release` workflow needs the private key as the repository secret `TAPQUEUE_RELEASE_KEY`
  (`gh secret set TAPQUEUE_RELEASE_KEY < ~/.config/tapqueue/release-key.pem`) and fails without it.
- The signature travels with the program: `tapqueue-admin clients publish` and **Check for
  updates** take it from the archive, and the console's upload forms have a field for the `.sig`.
  **Updates** shows which published builds have one.

If you build TapQueue yourself, your PCs and stations won't install your builds until they trust
your key: make one with `scripts/new-release-key.sh`, package with it, and put the public half it
prints in `client.toml` and `station.toml` as `update_key`. TapQueue's own releases still install.

The signature covers the program, not its version, so a server can offer an older release and PCs
will go back to it. That's how a bad update is rolled back; it also means a taken-over server
could bring back an old release with a known flaw.

## What a release contains

Server and station (`server-vX.Y.Z`):

| File | Contents |
|---|---|
| `TapQueue_server_X.Y.Z_linux-x64.tar.gz` | `tapqueue-server`, `tapqueue-admin`, `libe_sqlite3.so`, example config, systemd units, `install-server.sh`, `update-server.sh` |
| `TapQueue_station_X.Y.Z_linux-x64.tar.gz` | `tapqueue-station`, `tapqueue-station.sig`, example config, systemd unit, pcProx udev rule, `install-station.sh` |
| `SHA256SUMS` | checksums of the above |

`install.sh` in the repository root downloads the newest of these and runs the installer in it.
The console's **Check for updates** (Server page) looks for the newest `server-vX.Y.Z` release that
isn't a draft or pre-release and has the server archive and `SHA256SUMS` attached, so keep those
names as they are.

Clients (`client-vX.Y.Z`):

| File | Contents |
|---|---|
| `TapQueue_client_X.Y.Z.exe` | The Windows installer: what people download and run on a PC |
| `TapQueue_client_X.Y.Z_win-x64.zip` | `TapQueueClient.exe`, `TapQueueClient.exe.sig` and `version.txt`, for `tapqueue-admin clients publish` |
| `TapQueue_client_X.Y.Z_linux-x64.tar.gz` | `tapqueue-client`, `tapqueue-client.sig`, `version.txt`, example config, systemd unit, desktop entry, `install-client.sh`; installed by `install.sh client` and published with `tapqueue-admin clients publish` |
| `SHA256SUMS` | checksums of the above |

Build the server and station archives locally with `scripts/package.sh server`, the client zip
and Linux tarball with `scripts/package.sh client` (into `dist/`).
The installer can only be built on Windows: `scripts/package-client-installer.ps1`. CI builds it
on every push; download it from the run's artifacts.
