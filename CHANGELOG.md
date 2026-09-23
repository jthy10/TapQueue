# Changelog

All notable changes to TapQueue are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, a minor version bump (0.x.0) can contain
breaking changes; they're listed under **Changed** with what to do.

## [Unreleased]

### Changed
- **Breaking:** queues and printers are stored in the database instead of `server.toml`, and are
  managed with `tapqueue-admin queues …` and `tapqueue-admin printers …`. Changes take effect
  without restarting the server. The server refuses to start while `server.toml` still has
  `[[queues]]` or `[[printers]]`. To upgrade, re-create them with the admin CLI, then delete those sections.
- The database schema is versioned, and upgrades are applied automatically at startup.

### Added
- `tapqueue-admin printers add|edit|remove`, `queues`, `queues add|edit|remove`, `stations move`
  and `status`.
- Printers used by a release station can't be removed until the station is moved.
- `--version` on every program. The version, including the commit it was built from, also shows up
  in the server and station logs, `/healthz`, and the tray menu.
- Release archives for the server, station and Windows client, built by CI and attached to GitHub
  releases.
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

[Unreleased]: https://github.com/jthy10/TapQueue/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/jthy10/TapQueue/releases/tag/v0.1.0
