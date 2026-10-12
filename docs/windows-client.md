# Windows client

The TapQueue client for Windows 11 has two parts, both in `TapQueueClient.exe`:

- **The tray app**, started for each user when they sign in. It signs them in to the server
  (which is how their print jobs are matched to them), tells them when jobs are held, and lets
  them release or delete jobs.
- **The TapQueue service**, one per PC, running in the background as the system. It adds the
  TapQueue printers for every user and installs updates pushed from the server.

Windows prints with its built-in IPP driver, so there's no printer driver to install. For Linux
desktops, see the [Linux client](linux-client.md).

## Install

1. Download `TapQueue_client_X.Y.Z.exe` from [Releases](https://github.com/jthy10/TapQueue/releases)
   (the newest `client-v` release).
2. Run it (it asks for admin rights). It searches the network for TapQueue servers: pick yours
   from the list, or type its address, e.g. `https://tapqueue-server:8632`. The search only
   reaches the PC's own network; a server behind a router has to be typed in. Over `https://`,
   the TapQueue service trusts the server's self-signed certificate the first time it connects
   and adds it to the PC's trusted roots so Windows can print there; see [TLS](tls.md).
3. TapQueue starts in the tray. Within a minute the **TapQueue Secure Print** printer (or whatever
   the queue is called) appears for every user of the PC.
4. Print something to it, then tap a badge at a release station, or right-click the tray icon →
   **Release all to** → pick a printer.

The tray menu's **View printers and queue…** opens a window with two tabs:

- **Printers**: the queues you print to, and whether this PC has each installed as a printer, and
  the printers you can pick your pages up at, with where they are and how they're doing.
- **My print queue**: your jobs that are waiting, and the ones sent to a printer in the last day.
  Release the waiting ones here, or delete them. You can also delete a job that's already at the
  printer: with a 0.14 server, TapQueue asks the printer to stop it and gives back the pages that
  hadn't printed to your page limit, if you have one. A job that has finished printing stays as
  it is. (Double-clicking the tray icon opens this tab.)

If a TapQueue printer goes missing or stops working, or the tray app is out of sorts, choose
**Refresh TapQueue** in the tray menu. The TapQueue service checks with the server straight away,
then removes and adds each TapQueue printer again; the tray app then starts afresh, signs in again
and says how many printers were reinstalled (or what went wrong). Any user of the PC may do this;
it only ever installs the queues the server lists.

What goes where:

| | |
|---|---|
| `C:\Program Files\TapQueue\TapQueueClient.exe` | The program |
| `C:\ProgramData\TapQueue\client.toml` | Settings for the PC (see [`config/client.example.toml`](../config/client.example.toml)). Only admins can change it. |
| `C:\ProgramData\TapQueue\printers.txt` | The printers the service added, so they can be removed later |
| Services → **TapQueue** | The service; its log is in Event Viewer → Windows Logs → Application, source "TapQueue" |

To install on many PCs, run it silently, for example from a deployment tool or a login script:

```
TapQueue_client_X.Y.Z.exe /VERYSILENT /SERVER=https://tapqueue-server:8632
```

Without `/SERVER`, a first install uses the TapQueue server it finds on the network, and fails
(nothing is installed) if it finds none or more than one. Upgrades keep the server already set.

If the server asks new PCs for an enrollment code (see
[Security notes](how-it-works.md#security-notes)), add `/ENROLL=<code>`, or type it on the
installer's server page. It goes into `client.toml` as `enrollment_code`, and is only needed
until the PC has registered. With `install.ps1`, set `$env:TAPQUEUE_ENROLLMENT_CODE`.

Or from PowerShell run as administrator, which downloads the newest release, checks its
checksum and installs it silently:

```powershell
irm https://raw.githubusercontent.com/jthy10/TapQueue/main/install.ps1 | iex
```

Set `$env:TAPQUEUE_SERVER = "https://tapqueue-server:8632"` first to name the server. Run the same
command to repair a PC that stopped checking in or is stuck on an old version: it first prints the
service's state, the installed version, the server address and whether the server answers, then
reinstalls over what's there and starts the service and tray again.

To remove TapQueue, uninstall it from Settings → Apps. That also removes the printers and the
service. `client.toml` is kept, so reinstalling remembers the server.

The tray app has to be running for jobs to be matched to the user. See
[how jobs are matched to people](how-it-works.md#how-a-job-is-matched-to-a-person).

## Updates

PCs update themselves. To push the newest release to every PC, open **Updates** in the admin
console, press **Check for updates** under Clients and then **Publish**: the server downloads the
release from GitHub, checks it against its `SHA256SUMS` and hands it out (servers from 0.13). Or:

```
sudo tapqueue-admin clients update     # the newest release on GitHub, Windows and Linux
sudo tapqueue-admin clients            # each PC's version; "(updating)" until it has installed it
```

For a build that isn't a GitHub release, or a server that can't reach GitHub, upload it instead:
`sudo tapqueue-admin clients publish TapQueue_client_X.Y.Z_win-x64.zip`.

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

## Crash reports

If the TapQueue service or the tray app crashes, it sends the error with its full stack trace to
the server, so you don't have to go to the PC to find out why. A PC with a crash in the last day
shows as **Crashed** on the console's Workstations page (open it to read the report) and under
**Needs attention** on the Overview; each crash is also in the activity log. From the CLI:
`sudo tapqueue-admin crashes`, then `sudo tapqueue-admin crashes show <id>`.

A report that can't be sent straight away (the server is unreachable, or the crash was reading
`client.toml`) is kept on the PC and sent the next time the program starts. The server keeps the
newest 500.

## Known limits

- `client.toml` is for the whole PC, so on a shared PC with the server in `token` mode every user
  would sign in with the same token. Per-user sign-in for token mode is still to do; for now,
  shared PCs need `auth.mode = "dev"`, or a `username`/`token` per PC.
