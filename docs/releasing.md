# Releasing

TapQueue uses [Semantic Versioning](https://semver.org/). Before 1.0, bump the minor version
(0.**x**.0) for anything that breaks an existing install (config, API, database), and the patch
version for fixes. All programs share one version, set in `Directory.Build.props`.

Every build knows the commit it came from: `tapqueue-server --version` prints e.g. `0.2.0+1a2b3c4`.

## Day to day

Add a line under `## [Unreleased]` in `CHANGELOG.md` with each user-visible change: what changed
and, if it breaks something, what to do about it.

## Cutting a release

1. Pick the version and set `<Version>` in `Directory.Build.props`.
2. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`, add a fresh empty
   `## [Unreleased]` above it, and update the compare links at the bottom.
3. Commit ("Release X.Y.Z"), then tag and push:
   ```sh
   git tag -a vX.Y.Z -m "TapQueue X.Y.Z"
   git push origin main vX.Y.Z
   ```
4. The `release` workflow checks that the tag matches `<Version>`, runs the tests, builds the
   archives with `scripts/package.sh`, and publishes a GitHub release. The release notes are that
   version's section of `CHANGELOG.md`.
5. Set `<Version>` to the next expected version so development builds don't claim to be the release.

## What a release contains

| File | Contents |
|---|---|
| `tapqueue-server-X.Y.Z-linux-x64.tar.gz` | `tapqueue-server`, `tapqueue-admin`, `libe_sqlite3.so`, example config, systemd unit |
| `tapqueue-station-X.Y.Z-linux-x64.tar.gz` | `tapqueue-station`, example config, systemd unit, pcProx udev rule |
| `tapqueue-client-X.Y.Z-win-x64.zip` | `TapQueueClient.exe`, example config |
| `SHA256SUMS` | checksums of the above |

You can build the same archives locally with `scripts/package.sh`. They go into `dist/`.
