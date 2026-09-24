# Windows client

The TapQueue client for Windows 11 has two parts, both in `TapQueueClient.exe`:

- **The tray app**, started for each user when they sign in. It signs them in to the server
  (which is how their print jobs are matched to them), tells them when jobs are held, and lets
  them release or delete jobs.
- **The TapQueue service**, one per PC, running in the background as the system. It adds the
  TapQueue printers for every user and installs updates pushed from the server.

Windows prints with its built-in IPP driver, so there's no printer driver to install.

## Install

1. Download `TapQueue_client_X.Y.Z.exe` from [Releases](https://github.com/jthy10/TapQueue/releases)
   (the newest `client-v` release).
2. Run it (it asks for admin rights). It searches the network for TapQueue servers: pick yours
   from the list, or type its address, e.g. `http://tapqueue-server:8631`. The search only
   reaches the PC's own network; a server behind a router has to be typed in.
3. TapQueue starts in the tray. Within a minute the **TapQueue Secure Print** printer (or whatever
   the queue is called) appears for every user of the PC.
4. Print something to it, then tap a badge at a release station, or right-click the tray icon →
   **Release all to** → pick a printer.

What goes where:

| | |
|---|---|
| `C:\Program Files\TapQueue\TapQueueClient.exe` | The program |
| `C:\ProgramData\TapQueue\client.toml` | Settings for the PC (see [`config/client.example.toml`](../config/client.example.toml)). Only admins can change it. |
| `C:\ProgramData\TapQueue\printers.txt` | The printers the service added, so they can be removed later |
| Services → **TapQueue** | The service; its log is in Event Viewer → Windows Logs → Application, source "TapQueue" |

To install on many PCs, run it silently, for example from a deployment tool or a login script:

```
TapQueue_client_X.Y.Z.exe /VERYSILENT /SERVER=http://tapqueue-server:8631
```

Without `/SERVER`, a first install uses the TapQueue server it finds on the network, and fails
(nothing is installed) if it finds none or more than one. Upgrades keep the server already set.

To remove TapQueue, uninstall it from Settings → Apps. That also removes the printers and the
service. `client.toml` is kept, so reinstalling remembers the server.

The tray app has to be running for jobs to be matched to the user. See
[how jobs are matched to people](how-it-works.md#how-a-job-is-matched-to-a-person).

## Updates

PCs update themselves. To push a new build to every PC, download
`TapQueue_client_X.Y.Z_win-x64.zip` from the release and publish it on the server:

```
sudo tapqueue-admin clients publish TapQueue_client_X.Y.Z_win-x64.zip
sudo tapqueue-admin clients        # each PC's version; "(updating)" until it has installed it
```

The TapQueue service on each PC checks with the server once a minute. If its
`TapQueueClient.exe` differs from the newest published build, it downloads the build (the server
log shows each PC doing so), checks its SHA-256, replaces the exe and restarts. The tray apps
notice within a minute, restart into the new version and say "TapQueue updated". Publishing an
older build rolls PCs back the same way.

To update a PC without waiting, choose **Check for updates** in the tray menu. The tray app asks
the TapQueue service (over the local pipe `\\.\pipe\TapQueue`) to check with the server now; the
service installs the published build if it differs and the tray app restarts into it. Otherwise it
says the PC is up to date, that the server publishes no client, or why the update failed. It also
retries a build that failed to install before. If the service isn't running, it says so.

The download is checked against the hash the server sends, which catches damaged downloads but
not a tampered server or network. Signed builds and HTTPS are planned before production use.

## Known limits

- `client.toml` is for the whole PC, so on a shared PC with the server in `token` mode every user
  would sign in with the same token. Per-user sign-in for token mode is still to do; for now,
  shared PCs need `auth.mode = "dev"`, or a `username`/`token` per PC.
