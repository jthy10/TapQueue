# Windows client

The TapQueue client for Windows 11 has two parts, both in `TapQueueClient.exe`:

- **The tray app**, started for each user when they sign in. It tells them when jobs are held,
  lets them release or delete jobs and link their badge, and shows their balance.
- **The TapQueue service**, one per PC, running in the background as the system. It adds the
  TapQueue printers for every user, tells the server who is signed in to the PC (which is how
  print jobs are matched to people), and installs updates pushed from the server.

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

**Link my badge at** → a printer: for the next minute, a card nobody owns that's tapped on that
printer's reader becomes yours ([release stations](release-station.md#people-linking-their-own-badge)).
Where printing is [paid for](accounting.md), the menu shows your balance, and a release says what
it cost.

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

## Who you are

How the tray signs in is the server's choice ([how jobs are matched to
people](how-it-works.md#how-a-job-is-matched-to-a-person)):

- **Vouched for by the PC** (recommended): nothing to do. The TapQueue service signs in whoever
  is signed in to Windows, as the TapQueue user of the same name, and their jobs are theirs even
  if the tray app isn't running. The PC has to be installed with the server's enrollment code.
  Someone who isn't a TapQueue user sees "isn't set up to print with TapQueue" in the tray menu.
- **Name and password**: the tray shows **Not signed in**; **Sign in as…** asks for a domain
  account's name and password, or a local user's TapQueue password. It's remembered on that PC
  for that Windows user until they choose **Sign out**.
- **By token**: the `username` and `token` in `client.toml`, for a single person's PC or a test.
  With the two ways above, leave them empty.

## Updates

PCs update themselves. To push the newest release to every PC, open **Updates** in the admin
console, press **Check for updates** under Clients and then **Publish**: the server downloads the
release from GitHub, checks it against its `SHA256SUMS` and hands it out (servers from 0.13). Or:

```
sudo tapqueue-admin clients update     # the newest release on GitHub, Windows and Linux
sudo tapqueue-admin clients            # each PC's version; "(updating)" until it has installed it
```

Builds only come from TapQueue's releases on GitHub: there's no way to upload one to the server
(since server 0.17), so nobody with access to the console can hand PCs a program of their own. The
server has to be able to reach `api.github.com` and `github.com` to publish an update.

The TapQueue service on each PC checks with the server once a minute. If its
`TapQueueClient.exe` differs from the newest published build, it downloads the build (the server
log shows each PC doing so), checks its SHA-256, replaces the exe and restarts. The tray apps
notice within a minute, restart into the new version and say "TapQueue updated". Going back to an
older release works the same way: see [Downgrading](install-server.md#downgrading).

To update a PC without waiting, choose **Check for updates** in the tray menu. The tray app asks
the TapQueue service (over the local pipe `\\.\pipe\TapQueue`) to check with the server now; the
service installs the published build if it differs and the tray app restarts into it. Otherwise it
says the PC is up to date, that the server publishes no client, or why the update failed. It also
retries a build that failed to install before. If the service isn't running, it says so.

**Every PC checks an update itself.** The server only passes builds on, so someone who took over
the server could offer PCs any TapQueue release, but not a program of their own.

- **Signed releases** (clients from 0.19): each release's list of its programs is signed where
  the release is built, and the server hands that list and its signature out with the build. The
  TapQueue service checks the signature against the public key it was built with and installs
  the build only if it's on the list. Nothing but the TapQueue server has to be reachable.
  **Updates** in the console shows *Signature* beside such a build.
- **Otherwise** (a release that isn't signed, or a client older than 0.19): the service fetches
  the list of programs straight from the release on GitHub (`SHA256SUMS.programs`) and checks the
  build against that, so the PC needs to reach `github.com`. One that can't installs nothing,
  says why under **Workstations** in the console, and tries again every 10 minutes.
- A build with a signature the PC doesn't trust, or one that isn't in its release, is refused for
  good, and **Workstations** shows that too.
- If you build TapQueue yourself, sign your releases and set `update_key` in `client.toml` to your
  public key, or publish them with the same files and set `releases_url` to where they are
  ([releasing](releasing.md#signed-releases)).

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

- A token in `client.toml` is for the whole PC, so it only suits a PC one person uses. For shared
  PCs, labs and terminal servers, have the server sign people in as their PC's user, or with a
  name and password ([above](#who-you-are)).
