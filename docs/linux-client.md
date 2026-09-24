# Linux client

The TapQueue client for Linux desktops does what the [Windows client](windows-client.md) does, in
one program, `tapqueue-client`:

- **The tray app**, started for each user when they sign in to the desktop. It signs them in to
  the server (which is how their print jobs are matched to them), tells them when jobs are held,
  and lets them release or delete jobs.
- **The TapQueue service** (`tapqueue-client.service`), one per PC, running as root. It adds the
  TapQueue printers to CUPS for every user and installs updates pushed from the server.

CUPS prints to TapQueue as a driverless IPP Everywhere printer, so there's no driver to install.

Tested on Ubuntu 24.04 with GNOME. It needs systemd, CUPS (`lpadmin`), and a desktop that shows
tray icons (StatusNotifierItem/AppIndicator): Ubuntu's GNOME does out of the box, as do KDE
Plasma, Cinnamon, Xfce and most others. Stock GNOME without Ubuntu's changes needs the
[AppIndicator extension](https://extensions.gnome.org/extension/615/appindicator-support/). It's
built for x86_64.

## Install

```
curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s client
```

That downloads the newest `client-v` release's `TapQueue_client_X.Y.Z_linux-x64.tar.gz`, checks its
SHA-256 and runs the `install-client.sh` inside (you can also download it, extract it and run
`sudo ./install-client.sh`). It searches the network for TapQueue servers: pick yours from the list,
or type its address, e.g. `http://tapqueue-server:8631`. The search only reaches the PC's own
network; a server behind a router has to be typed in.

Within a minute the **TapQueue Secure Print** printer (or whatever the queue is called) appears in
every print dialog. The tray app starts the next time someone signs in to the desktop; to start it
now, open **TapQueue** from the app menu. Print something to it, then tap a badge at a release
station, or click the tray icon → **Release all to** → pick a printer.

What goes where:

| | |
|---|---|
| `/opt/tapqueue-client/tapqueue-client` | The program: a symlink to the current build in `builds/` (see [Updates](#updates)) |
| `/etc/tapqueue/client.toml` | Settings for the PC (see [`config/client.example.toml`](../config/client.example.toml)). Only root can change it. |
| `/var/lib/tapqueue-client/printers.txt` | The printers the service added, so they can be removed later |
| `tapqueue-client.service` | The service; its log: `journalctl -u tapqueue-client` |
| `/etc/xdg/autostart/tapqueue-client.desktop` | Starts the tray app when anyone signs in to the desktop |
| `/run/tapqueue-client/update.sock` | Where the tray app asks the service to check for updates |

The printer's CUPS name is the queue name with spaces (and `/ \ # ? ' "`) made underscores, e.g.
`TapQueue_Secure_Print`; print dialogs show the queue name itself. The service doesn't share the
printers on the network.

To install on many PCs without questions, set the server:

```
curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo TAPQUEUE_SERVER=http://tapqueue-server:8631 bash -s client
```

Upgrades (run it again) keep the server already set. To remove TapQueue:

```
sudo /path/to/extracted/install-client.sh --uninstall
```

That removes the printers, the service, the autostart entry and the program. `client.toml` is
kept, so reinstalling remembers the server.

The tray app has to be running for jobs to be matched to the user. See
[how jobs are matched to people](how-it-works.md#how-a-job-is-matched-to-a-person).

## Updates

PCs update themselves, as on Windows. Publish the Linux tarball from the release on the server:

```
sudo tapqueue-admin clients publish TapQueue_client_X.Y.Z_linux-x64.tar.gz
sudo tapqueue-admin workstations   # each PC's OS and version; "(behind)" until it has installed it
```

Windows and Linux builds are published separately; each PC installs the newest build for its own
platform. The service checks with the server once a minute, downloads a build that differs from
the one it runs, checks its SHA-256, and restarts into it. **Check for updates** in the tray menu
asks the service to do that now. Tray apps restart into the new build within a minute.

A single-file .NET program reads parts of itself from its own file while it runs, so replacing
the file under a running tray app would crash it. Instead each build is kept in its own file in
`/opt/tapqueue-client/builds/`, and an update points the `tapqueue-client` symlink at the new one.
The service deletes a build once no process runs it.

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

- `client.toml` is for the whole PC and readable by every user (each user's tray app reads it),
  so with the server in `token` mode every user would sign in with the same token. Per-user sign-in
  for token mode is still to do; for now, shared PCs need `auth.mode = "dev"`.
- The tray app uses about 150–200 MB of memory (the UI toolkit, Avalonia, is bundled into the
  program so nothing has to be installed from the distribution).
- x86_64 only for now.
