# Windows client

`TapQueueClient.exe` is a tray app for Windows 11. It signs the user in to the server, adds the
TapQueue printer, tells them when jobs are held, and lets them release or delete jobs.
Windows prints with its built-in IPP driver, so there's no printer driver to install.

## Install

1. Copy `TapQueueClient.exe` from `tapqueue-client-X.Y.Z-win-x64.zip`
   ([Releases](https://github.com/jthy10/TapQueue/releases)) to the PC.
2. Create `C:\ProgramData\TapQueue\client.toml` (see [`config/client.example.toml`](../config/client.example.toml)):
   ```toml
   server_url = "http://tapqueue-server:8631"
   username = "jsmith"     # empty = use the Windows username
   token = "…"             # from `tapqueue-admin users add`; not needed in dev mode
   ```
3. Run `TapQueueClient.exe`. It signs in and adds the TapQueue printer, then sits in the tray.
   The first run may need to be **as administrator**, so that Windows lets it add the printer.
4. Print something to **TapQueue Secure Print** (or whatever the queue is called), then tap a badge
   at a release station, or right-click the tray icon → **Release all to** → pick a printer.

The client has to be running for jobs to be matched to the user. See
[how jobs are matched to people](how-it-works.md#how-a-job-is-matched-to-a-person).

## Updates

Clients update themselves. To push a new build to every PC, publish the release zip on the server:

```
sudo tapqueue-admin clients publish tapqueue-client-X.Y.Z-win-x64.zip
sudo tapqueue-admin clients        # each client's version; "(updating)" until it has installed it
```

Each client checks with the server when it signs in and on every heartbeat (once a minute). If its
own `TapQueueClient.exe` differs from the newest published build, it downloads the build, checks
its SHA-256, replaces its exe, restarts, and shows a "TapQueue updated" notification. Publishing an
older build rolls clients back the same way.

- The exe replaces itself in place, so the user running it must be able to write to its folder.
  Keep it somewhere like `%LocalAppData%\TapQueue`, not `Program Files`. If it can't write there,
  it shows "Couldn't update TapQueue" and keeps running the old version.
- The download is checked against the hash the server sends, which catches damaged downloads but
  not a tampered server or network. Signed builds and HTTPS are planned before production use.
- Only clients from 0.2.0 on update themselves; install that version by hand once.
