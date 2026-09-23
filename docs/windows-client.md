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
