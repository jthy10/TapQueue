# Changelog: clients

All notable changes to the TapQueue Windows client (`TapQueue_client_X.Y.Z.exe`) and, from 0.5.0,
the Linux client (`TapQueue_client_X.Y.Z_linux-x64.tar.gz`) are recorded here.
It has its own version, released with tags `client-vX.Y.Z`; the server and station have
[their own changelog](CHANGELOG.md). Up to 0.1.0 everything shared one version.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.5.0] - 2026-09-24

### Added
- Linux client, for desktops such as Ubuntu with GNOME: the same tray app (held jobs, release,
  cancel, notifications, Check for updates) and a root systemd service that adds the queues to CUPS
  as driverless printers and installs builds pushed from the server. Install it with
  `curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s client`.
  See [docs/linux-client.md](docs/linux-client.md).
- Crash reports: when the TapQueue service or tray app crashes it sends the error to the server,
  where admins read it on the console (Workstations) or with `tapqueue-admin crashes`. Needs
  server 0.5.0; until then reports wait on the PC.

### Fixed
- The Windows service now really restarts after installing a pushed update. It stopped with the
  wrong kind of exit code, so Windows left it stopped, and "Check for updates" then said the service
  wasn't running. PCs on 0.4 or older still stop once when they install 0.5: start the TapQueue
  service (or restart the PC) after that one update.
- A client no longer installs a build for another platform (a server older than 0.5 offers its
  Windows build to every PC).
- Tray apps that can't reach the server still restart into a newly installed build.

### Changed
- The Windows client tells the server it's a Windows PC when it checks in and signs in, so it gets
  the Windows build now that the server publishes one per platform. Needs server 0.5.0 or later
  to tell platforms apart; older servers ignore it.

## [0.4.0] - 2026-09-24

### Added
- The installer searches the network for TapQueue servers and lists them on its server page;
  with only one, its address is filled in. A silent first install without `/SERVER` uses the one
  server it finds.

## [0.3.0] - 2026-09-23

### Added
- "Check for updates" in the tray menu: the TapQueue service checks with the server straight
  away and installs the published client if it differs, and the tray app restarts into it or says
  it's up to date, that the server has no client published, or why the update failed.
- The TapQueue service tells the server which PC it's on, which client it runs and why its last
  update failed, so the PC shows on the console's Workstations page. It installs the published
  client straight away when an admin asks for an update, even one that failed before.
- When an admin signs someone out, the tray app says so and stays signed out (instead of signing
  straight back in) until they choose "Sign in again" or sign in to Windows again.

## [0.2.0] - 2026-09-23

### Added
- Installer, `TapQueue_client_X.Y.Z.exe`: installs for the whole PC (Program Files), asks for the
  server address and keeps it in `C:\ProgramData\TapQueue\client.toml`, starts TapQueue for every
  user at sign-in, and can be removed from Settings > Apps. Silent installs:
  `TapQueue_client_X.Y.Z.exe /VERYSILENT /SERVER=http://tapqueue-server:8631`.
- The TapQueue Windows service adds the TapQueue printers for every user (no admin rights needed
  at sign-in) and keeps them matching the server's queues.
- Automatic updates: the service installs the client build published on the server within about
  a minute, and the tray app restarts into it and says it was updated.

### Changed
- The tray app no longer adds printers; the service does. "Reinstall TapQueue printer" is gone
  from the tray menu.

## [0.1.0] - 2026-09-23

First working version: a tray app configured by a file that signs in, adds the printer, and shows
and releases held jobs.

[Unreleased]: https://github.com/jthy10/TapQueue/compare/client-v0.5.0...HEAD
[0.5.0]: https://github.com/jthy10/TapQueue/compare/client-v0.4.0...client-v0.5.0
[0.4.0]: https://github.com/jthy10/TapQueue/compare/client-v0.3.0...client-v0.4.0
[0.3.0]: https://github.com/jthy10/TapQueue/compare/client-v0.2.0...client-v0.3.0
[0.2.0]: https://github.com/jthy10/TapQueue/compare/v0.1.0...client-v0.2.0
[0.1.0]: https://github.com/jthy10/TapQueue/releases/tag/v0.1.0
