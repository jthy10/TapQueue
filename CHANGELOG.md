# Changelog: server and station

All notable changes to the TapQueue server, release station and `tapqueue-admin` are recorded
here; they share a version and are released together with tags `server-vX.Y.Z`. The Windows
client has its own version and [changelog](CHANGELOG-client.md). Up to 0.1.0 everything shared one version.

The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, a minor version bump (0.x.0) can contain
breaking changes; they're listed under **Changed** with what to do.

## [Unreleased]

### Added
- Admin console at `http://<server>:8631/admin`: an overview of what needs attention, jobs
  (release or cancel any held job), users, cards (enroll one by tapping it at a station),
  printers, queues, stations, workstations, client updates and server status. It has no sign-in
  yet, so it's only served with `auth.mode = "dev"`. See [docs/admin-ui.md](docs/admin-ui.md).
- Admin API: `GET /server`, `GET /jobs/{id}` and `DELETE /jobs/{id}` (cancel a held job).
- Users can be renamed, disabled and deleted. A disabled user is signed out, can't sign in, has
  new jobs refused and can't release; their held jobs are kept. Deleting a user cancels their held
  jobs and removes their cards; job history keeps their name.
- Cards can have a label and be moved to another user.
- Activity log of admin changes, prints, releases, badge taps and sign-ins, kept for 90 days
  (`GET /api/v1/admin/events`), shown on the console's Activity page and in each user's,
  printer's and station's details.

### Changed
- With `auth.mode = "dev"`, the admin API accepts requests without the admin token.

## [0.2.0] - 2026-09-23

### Changed
- Releases are tagged `server-vX.Y.Z`; the Windows client is released separately as `client-vX.Y.Z`.
- **Breaking:** queues and printers are stored in the database instead of `server.toml`, and are
  managed with `tapqueue-admin queues …` and `tapqueue-admin printers …`. Changes take effect
  without restarting the server. The server refuses to start while `server.toml` still has
  `[[queues]]` or `[[printers]]`. To upgrade, re-create them with the admin CLI, then delete those sections.
- The database schema is versioned, and upgrades are applied automatically at startup.

### Added
- One-command installs from GitHub:
  `curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s server`
  (or `station`). Run it again to upgrade.
- `install-station.sh` in the station archive: asks for the server and station token, finds a
  pcProx or keyboard-style reader, and installs the service.
- Pushing Windows client updates: `tapqueue-admin clients publish TapQueue_client_X.Y.Z_win-x64.zip`
  makes every PC install that build within about a minute. `tapqueue-admin clients` shows which
  version each PC runs, and the log shows each PC downloading it.
  See [docs/windows-client.md](docs/windows-client.md#updates).
- Optional `tapqueue-console` service shows the server's live log on its own screen.
- The server logs one line per event.
- `tapqueue-admin printers add|edit|remove`, `queues`, `queues add|edit|remove`, `stations move`
  and `status`.
- Printers used by a release station can't be removed until the station is moved.
- `--version` on every program. The version, including the commit it was built from, also shows up
  in the server and station logs, `/healthz`, and the tray menu.
- Release archives (`TapQueue_server_X.Y.Z_linux-x64.tar.gz`, `TapQueue_station_…`), built by CI
  and attached to GitHub releases.
- `install-server.sh` in the server archive installs or upgrades the server in one step.
- `tapqueue-admin` run with `sudo` on the server reads the admin token from `/etc/tapqueue/server.toml`.
- Documentation split into `docs/`, including how jobs are matched to people and where that
  falls short (NAT, terminal servers).
- End-to-end tests for printing, holding, releasing and badge taps against a fake printer.

## [0.1.0] - 2026-09-23

First working version.

### Added
- IPP hold queue that Windows 11 prints to with its built-in driver; jobs are held per user.
- Release to a physical printer over IPP, keeping print options; retries while the printer is busy.
- Windows tray client configured by a file: signs in, adds the printer, shows and releases held jobs.
- `tapqueue-admin` for users, jobs, printers, badges and stations.
- `tapqueue-station`: release station for keyboard-style USB badge readers and RFIDeas pcProx
  readers. A tap releases the user's held jobs to the station's printer.
- Badge enrollment by an admin (`badges add --last-tap`).

[Unreleased]: https://github.com/jthy10/TapQueue/compare/server-v0.2.0...HEAD
[0.2.0]: https://github.com/jthy10/TapQueue/compare/v0.1.0...server-v0.2.0
[0.1.0]: https://github.com/jthy10/TapQueue/releases/tag/v0.1.0
