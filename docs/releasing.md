# Releasing

TapQueue has two release tracks, each with its own version, changelog and tag:

| Track | Version in `Directory.Build.props` | Changelog | Tag |
|---|---|---|---|
| Server, station and `tapqueue-admin` | `<Version>` | `CHANGELOG.md` | `server-vX.Y.Z` |
| Windows client | `<TapQueueClientVersion>` | `CHANGELOG-client.md` | `client-vX.Y.Z` |

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
   with the tests; client: on Windows, with the installer), and publishes a GitHub release whose
   notes are that version's section of the changelog.
5. Set the version to the next expected one so development builds don't claim to be the release.

For a client release, push it to your PCs afterwards:
`sudo tapqueue-admin clients publish TapQueue_client_X.Y.Z_win-x64.zip` on the server.

## What a release contains

Server and station (`server-vX.Y.Z`):

| File | Contents |
|---|---|
| `TapQueue_server_X.Y.Z_linux-x64.tar.gz` | `tapqueue-server`, `tapqueue-admin`, `libe_sqlite3.so`, example config, systemd units, `install-server.sh` |
| `TapQueue_station_X.Y.Z_linux-x64.tar.gz` | `tapqueue-station`, example config, systemd unit, pcProx udev rule, `install-station.sh` |
| `SHA256SUMS` | checksums of the above |

`install.sh` in the repository root downloads the newest of these and runs the installer in it.

Windows client (`client-vX.Y.Z`):

| File | Contents |
|---|---|
| `TapQueue_client_X.Y.Z.exe` | The installer: what people download and run on a PC |
| `TapQueue_client_X.Y.Z_win-x64.zip` | `TapQueueClient.exe` and `version.txt`, for `tapqueue-admin clients publish` |
| `SHA256SUMS` | checksums of the above |

Build the server and station archives locally with `scripts/package.sh server` (into `dist/`).
The installer can only be built on Windows: `scripts/package-client-installer.ps1`. CI builds it
on every push; download it from the run's artifacts.
