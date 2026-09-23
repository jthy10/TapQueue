# Changelog: Windows client

All notable changes to the TapQueue Windows client (`TapQueue_client_X.Y.Z.exe`) are recorded here.
It has its own version, released with tags `client-vX.Y.Z`; the server and station have
[their own changelog](CHANGELOG.md). Up to 0.1.0 everything shared one version.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/jthy10/TapQueue/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/jthy10/TapQueue/releases/tag/v0.1.0
