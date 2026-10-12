# Changelog: clients

All notable changes to the TapQueue Windows client (`TapQueue_client_X.Y.Z.exe`) and, from 0.5.0,
the Linux client (`TapQueue_client_X.Y.Z_linux-x64.tar.gz`) are recorded here.
It has its own version, released with tags `client-vX.Y.Z`; the server and station have
[their own changelog](CHANGELOG.md). Up to 0.1.0 everything shared one version.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.18.0] - 2026-10-12

No changes to the clients themselves: released with server 0.18.0, so one number still says what
a site runs.

## [0.17.0] - 2026-10-12

From this release the clients carry the same version number as the server and station, so one
number says what a site runs. This one is 0.17.0; there is no client 0.11 to 0.16.

### Changed
- **Updates are checked against GitHub before they're installed.** The TapQueue service used to
  install whatever build its server published, checking only a checksum the server itself sent.
  It now fetches the release's list of programs (`SHA256SUMS.programs`) from GitHub and installs
  the build only if it's on it, so a server that was taken over can pass on TapQueue releases but
  can't run a program of its own on every PC. PCs need to reach `github.com` to update; one that
  can't says so under **Workstations** and tries again every 10 minutes. `releases_url` in
  `client.toml` is for sites that build their own TapQueue.

### Added
- **My print queue** says whether a job you released is still **printing** or has **printed**,
  with a 0.17 server and a printer that can be asked.
- **The tray says why a tap at a printer printed nothing**, with a 0.17 server: nothing was
  waiting, you're over your page limit, the printer can't print right now, or you aren't allowed
  to print there. It also says when a printer gave up on a job and it's waiting again.
- Releasing from the tray says when a job won't come out as it was sent: in black and white, or
  on one side, because the printer you chose can't do otherwise.

## [0.10.0] - 2026-10-12

### Added
- **View printers and queue…** in the tray menu (Windows and Linux), in place of **Held jobs…**.
  It opens a window with two tabs. **Printers** lists the queues you print to, with whether this
  PC has each one installed, and the printers you can pick up at, with their location, status and
  model. **My print queue** lists your waiting jobs and the ones sent to a printer in the last
  day, with their pages, and is where you release or delete them.
- Deleting a job that's already at the printer. With a 0.14 server, TapQueue asks the printer to
  stop it and gives back the pages that hadn't printed to your page limit, and tells you what
  happened. Older servers still only delete waiting jobs, and the window says so.

### Changed
- **Refresh printers** in the tray menu is now **Refresh TapQueue**: it reinstalls the PC's
  TapQueue printers as before, then starts the tray app afresh, so it signs in again and picks up
  anything that changed on the server.

## [0.9.7] - 2026-10-11

### Fixed
- The TapQueue service read whatever a program on the PC sent to its local pipe (Windows) or
  socket (Linux) until the line ended, so any user of the PC could make it use a lot of memory for
  ten seconds at a time. It now drops a request longer than a tray app's could be.

## [0.9.6] - 2026-10-09

### Fixed
- Two queues with the same name on the server made the TapQueue service crash and restart every
  minute, on every PC, so printers weren't kept up to date and new client builds weren't installed.
  The PC now adds the first of them and carries on (and servers from 0.10 refuse the second name).

## [0.9.5] - 2026-10-09

### Added
- `enrollment_code` in `client.toml`, for servers (0.9.5 and later) that only let new PCs register
  with their enrollment code. The Windows installer takes `/ENROLL=<code>` and has a field for it
  on the server page; `install.sh` and `install.ps1` take `TAPQUEUE_ENROLLMENT_CODE`. Only sent
  until the PC has its key.

### Changed
- With server 0.9.5, printing while nobody is signed in to TapQueue on the PC gives a print error
  asking you to sign in, instead of the job disappearing into a queue nobody could release it from.

## [0.8.0] - 2026-10-08

### Added
- TLS: with `server_url = "https://<server>:8632"` (server 0.8.0 and later) everything, printing
  included, is encrypted. The TapQueue service trusts the server's self-signed certificate the
  first time it connects and the tray app trusts the same one, or set `server_cert_fingerprint` in
  `client.toml`. On Windows the service adds that certificate to the PC's trusted roots so the
  built-in IPP driver can print over `https://`, refusing one that could stand in for other sites.
  The installers offer the server's `https://` address when it has one. See [tls.md](docs/tls.md).
- **Refresh printers** in the tray menu (Windows and Linux): the TapQueue service checks with the
  server straight away and removes and adds each TapQueue printer again, for when one has gone
  missing or stopped working.
- Per-job identity (needs server 0.8). The TapQueue service gets a key for the PC from the server,
  re-adds the printers once with a print key made from it, and vouches for each tray app that signs
  in on the PC, from the account Windows or Linux says is running it. Jobs are then matched to the
  PC user who printed them rather than the PC's address. The key is in `workstation-key.json` next
  to `printers.txt`, readable only by SYSTEM and administrators (root on Linux).

## [0.7.0] - 2026-09-25

### Added
- `install.ps1`: install, upgrade or repair the Windows client from PowerShell
  (`irm https://raw.githubusercontent.com/jthy10/TapQueue/main/install.ps1 | iex`), with a
  report of what state the client was in first.

## [0.6.0] - 2026-09-24

### Added
- **Sign in as…** and **Sign out** in the tray menu (Windows and Linux), for servers set to have
  people sign in with their domain account. Sign in as… is greyed out when the server signs people
  in as the PC's user (the default). The sign-in is remembered in the PC user's profile until they
  sign out. Needs server 0.6.0. See
  [active-directory.md](docs/active-directory.md#tray-sign-in).

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

[Unreleased]: https://github.com/jthy10/TapQueue/compare/client-v0.18.0...HEAD
[0.18.0]: https://github.com/jthy10/TapQueue/compare/client-v0.17.0...client-v0.18.0
[0.17.0]: https://github.com/jthy10/TapQueue/compare/client-v0.10.0...client-v0.17.0
[0.10.0]: https://github.com/jthy10/TapQueue/compare/client-v0.9.7...client-v0.10.0
[0.9.7]: https://github.com/jthy10/TapQueue/compare/client-v0.9.6...client-v0.9.7
[0.9.6]: https://github.com/jthy10/TapQueue/compare/client-v0.9.5...client-v0.9.6
[0.9.5]: https://github.com/jthy10/TapQueue/compare/client-v0.8.0...client-v0.9.5
[0.8.0]: https://github.com/jthy10/TapQueue/compare/client-v0.7.0...client-v0.8.0
[0.7.0]: https://github.com/jthy10/TapQueue/compare/client-v0.6.0...client-v0.7.0
[0.6.0]: https://github.com/jthy10/TapQueue/compare/client-v0.5.0...client-v0.6.0
[0.5.0]: https://github.com/jthy10/TapQueue/compare/client-v0.4.0...client-v0.5.0
[0.4.0]: https://github.com/jthy10/TapQueue/compare/client-v0.3.0...client-v0.4.0
[0.3.0]: https://github.com/jthy10/TapQueue/compare/client-v0.2.0...client-v0.3.0
[0.2.0]: https://github.com/jthy10/TapQueue/compare/v0.1.0...client-v0.2.0
[0.1.0]: https://github.com/jthy10/TapQueue/releases/tag/v0.1.0
