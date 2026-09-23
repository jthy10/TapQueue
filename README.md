# TapQueue

Open source print management with badge release. Users print to one shared queue,
and their jobs wait on the server until they walk up to any printer and release them there.
Nothing sits in the output tray for someone else to pick up.

> **Status: early development (v0.1).** Print → hold → release works end to end against a real
> printer, from the tray app or by tapping a badge at a release station. Expect breaking changes.

```
 Windows 11 PC                     TapQueue server (Linux)                 At the printer
┌─────────────────────┐   IPP    ┌─────────────────────────────┐        ┌──────────────────┐
│ "TapQueue Secure    │ ───────▶ │ Holds jobs per user         │ badge  │ Release station  │
│  Print" printer     │          │ Users, sessions, printers   │ ◀───── │ (card reader)    │
│  (built-in driver)  │          │ Releases to the printer     │        │                  │
│ TapQueue tray app   │ ◀──────▶ │                             │  IPP   ┌──────────┐
└─────────────────────┘  REST    └─────────────────────────────┘ ─────▶ │ Printer  │
                                                                         └──────────┘
```

## What's in the box

| Component | Path | Runs on |
|---|---|---|
| **tapqueue-server**: IPP hold queue, REST API, releases jobs to printers | `src/TapQueue.Server` | Linux |
| **tapqueue-admin**: command-line admin tool | `src/TapQueue.Admin` | Linux (anywhere .NET runs) |
| **TapQueueClient.exe**: tray app, adds the printer, shows held jobs | `src/TapQueue.Client.Windows` | Windows 11 |
| **tapqueue-station**: USB badge reader next to a printer; a tap releases your jobs | `src/TapQueue.Station` | Linux |

## How it works

1. **The server looks like an ordinary network printer.** Each configured queue is an
   [IPP Everywhere](https://www.pwg.org/ipp/everywhere.html) printer. Windows 11 prints to it
   with its built-in IPP class driver, so there is no driver to install. You choose the name users
   see in the print dialog (e.g. "TapQueue Secure Print").
2. **Jobs are held, not printed.** The server stores the document (usually PDF) together with the
   options chosen in the print dialog (copies, page ranges, orientation…).
3. **The job is matched to a person.** The username inside a print job is easy to fake, so the
   server doesn't rely on it. The TapQueue tray app signs in to the server and keeps a session open.
   A job belongs to the user who is signed in on the machine that sent it.
4. **The user releases it at a printer** by tapping their badge on the release station next to it
   (or from the tray app). The server sends every held job, with its print options, straight to that
   printer over IPP. The station never touches the documents and doesn't need to be connected to
   the printer. It only needs to reach the server over the network.
5. **Unreleased jobs expire** after `hold_hours` (default 24) and are deleted from disk.

Users, badges and sign-in methods all point at the same user record. Moving from "username in a
config file" to "tap your badge" adds a way to identify the same user and doesn't change anything else.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.

### 1. Server (Ubuntu 24.04)

```sh
git clone https://github.com/jthy10/TapQueue.git && cd TapQueue
dotnet publish src/TapQueue.Server -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
dotnet publish src/TapQueue.Admin  -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out

sudo useradd --system --home-dir /var/lib/tapqueue --shell /usr/sbin/nologin tapqueue
sudo install -d /opt/tapqueue /etc/tapqueue
sudo install -m 755 out/tapqueue-server out/tapqueue-admin /opt/tapqueue/
sudo install -m 640 -g tapqueue config/server.example.toml /etc/tapqueue/server.toml
sudoedit /etc/tapqueue/server.toml   # set admin.token, your queue name and your printer's address
sudo install -m 644 deploy/systemd/tapqueue-server.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now tapqueue-server
journalctl -u tapqueue-server -f     # look for "Printer ... is online"
```

Most network printers accept IPP at `ipp://<printer-ip>/ipp/print`. You can check with
`ipptool -tv ipp://<printer-ip>/ipp/print get-printer-attributes.test`.

### 2. Users

```sh
export TAPQUEUE_ADMIN_TOKEN=<admin.token from server.toml>
/opt/tapqueue/tapqueue-admin users add jsmith --name "Jane Smith"   # prints the user's client token
```

For a quick test you can set `auth.mode = "dev"` instead. Clients then only need a username, and
users are created the first time they sign in. **Don't use dev mode on a network you don't trust.**

### 3. Windows 11 client

1. Copy `TapQueueClient.exe` (from `dotnet publish src/TapQueue.Client.Windows -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`,
   or from the CI artifacts) to the PC.
2. Create `C:\ProgramData\TapQueue\client.toml` (see [`config/client.example.toml`](config/client.example.toml)):
   ```toml
   server_url = "http://tapqueue-server:8631"
   username = "jsmith"     # empty = use the Windows username
   token = "…"             # not needed in dev mode
   ```
3. Run `TapQueueClient.exe`. It signs in and adds the TapQueue printer. It then sits in the tray and
   tells you when jobs are held. The first run may need to be **as administrator**, so that Windows
   lets it add the printer.
4. Print something to **TapQueue Secure Print**, then right-click the tray icon → **Release all to** → pick a printer.

### 4. Release station (badge reader)

Any small Linux box next to the printer with a USB badge reader plugged in. Two kinds of reader work:

- **Keyboard-style readers** (`reader = "keyboard"`, most USB readers) "type" the card number and
  press Enter. The station reads the reader directly from `/dev/input` and grabs it, so card
  numbers don't end up typed into a login prompt.
- **RFIDeas pcProx** (`reader = "pcprox"`) is polled over its HID feature-report channel instead,
  which also works when the reader is set to "SDK mode" and types nothing. Install
  `deploy/udev/60-tapqueue-pcprox.rules` so the service can open it. Based on
  [ID-Card-Reader](https://github.com/jthy10/ID-Card-Reader), whose notes explain the reader's quirks.

On the server, create the station and choose which `[[printers]]` entry it releases to:

```sh
tapqueue-admin stations add lobby office     # prints the station token
```

On the station (x86_64 Ubuntu; build with
`dotnet publish src/TapQueue.Station -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out`,
or get it from the CI artifacts):

```sh
sudo useradd --system --no-create-home --shell /usr/sbin/nologin --groups input tapqueue-station
sudo install -d /opt/tapqueue /etc/tapqueue
sudo install -m 755 tapqueue-station /opt/tapqueue/

/opt/tapqueue/tapqueue-station --list-devices                    # find the reader
sudo /opt/tapqueue/tapqueue-station --test --device /dev/input/by-id/usb-…-event-kbd
                                                                 # tap a card; its number is printed
sudo /opt/tapqueue/tapqueue-station --test --reader pcprox       # same, for a pcProx
sudo install -m 644 deploy/udev/60-tapqueue-pcprox.rules /etc/udev/rules.d/   # pcProx only
sudo udevadm control --reload-rules && sudo udevadm trigger
sudo install -m 640 -g tapqueue-station config/station.example.toml /etc/tapqueue/station.toml
sudoedit /etc/tapqueue/station.toml          # server_url, token, reader, device
sudo install -m 644 deploy/systemd/tapqueue-station.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now tapqueue-station
journalctl -u tapqueue-station -f            # "Station "lobby" releases to Office printer (online)."
```

**Enrolling badges.** Tap a new card at any station. The station reports it as unrecognized. Then link it:

```sh
tapqueue-admin badges add jsmith --last-tap  # links the card most recently tapped anywhere
tapqueue-admin badges add jsmith 04A1B2C3    # or type the number if you already know it
```

A tap then sends all of that user's held jobs to the station's printer. If a job can't be sent,
it stays held so the user can try again at another printer.

> Card numbers from cheap 125 kHz and MIFARE readers are easy to copy, just like a building badge.
> TapQueue treats a tap as "this person is standing at the printer", nothing stronger.

## Admin CLI

```
tapqueue-admin users                           List users
tapqueue-admin users add <username> [--name]   Create a user and print their client token
tapqueue-admin users reset-token <username>    Issue a new client token
tapqueue-admin jobs [--status held]            List recent jobs
tapqueue-admin printers [--refresh]            Printer reachability and status
tapqueue-admin release <user> <printer> [ids]  Release a user's held jobs to a printer
tapqueue-admin badges [username]               List badges
tapqueue-admin badges add <user> <card>|--last-tap
                                               Link a badge to a user
tapqueue-admin badges unknown                  Unrecognized cards tapped in the last hour
tapqueue-admin badges remove <badge-id>        Unlink a badge
tapqueue-admin stations                        Release stations and when they last checked in
tapqueue-admin stations add <id> <printer>     Create a station and print its token
tapqueue-admin stations reset-token <id>       Issue a new station token
tapqueue-admin stations remove <id>            Delete a station
```

Connection settings come from `--server`/`--token`, `TAPQUEUE_SERVER`/`TAPQUEUE_ADMIN_TOKEN`, or
`~/.config/tapqueue/admin.toml`.

## Development

```sh
dotnet build          # builds everything, including the Windows client (on Linux too)
dotnet test
dotnet run --project src/TapQueue.Server -- --config path/to/server.toml
```

The server's IPP endpoint is checked with the CUPS conformance suites:

```sh
ipptool -t -f /usr/share/cups/data/testprint ipp://localhost:8631/ipp/secure ipp-1.1.test
ipptool -t -f /usr/share/cups/data/testprint ipp://localhost:8631/ipp/secure ipp-everywhere.test
```

`ipp-1.1.test` passes. `ipp-everywhere.test` has two known differences:
- The suite expects printer supply levels (toner and so on). A hold queue has no supplies, so it
  reports them as `unknown`.
- The suite looks for `overrides-supported = document-number`, but the spec keyword (and the one
  CUPS' own `ipp-2.2.test` checks for) is `document-numbers`, which is what we send.

### Layout

```
src/TapQueue.Server/          ASP.NET Core server
  Ipp/                        IPP encoder/decoder, queue endpoint, printer client
  Jobs/                       spool, owner matching, release, cleanup
  Data/                       SQLite storage
  Api/                        REST API for clients and admins
src/TapQueue.Admin/           tapqueue-admin
src/TapQueue.Station/         tapqueue-station (badge reader service)
src/TapQueue.Client.Windows/  WinForms tray client
src/TapQueue.Shared/          API types and config loading shared by all of the above
tests/                        unit tests
config/                       example config files
deploy/                       systemd units, udev rule
```

## Roadmap

- [x] IPP hold queue that Windows 11 can print to with no driver install
- [x] Hold jobs per user; release to a physical printer over IPP, keeping print options
- [x] Windows tray client configured by a file
- [x] Admin CLI
- [x] Release station: Linux service for a USB badge reader next to each printer
- [x] Badge enrollment by an admin (tap, then `badges add --last-tap`)
- [ ] Feedback at the printer (screen or beeper) for "nothing to print" and errors
- [ ] Self-service badge enrollment from the tray app
- [ ] TLS for client and IPP connections
- [ ] Web admin console
- [ ] Directory sync (Active Directory / LDAP / Entra ID)
- [ ] Page counting, quotas and reports
- [ ] Installers (.deb, MSI) and ARM builds for small release devices

## License

[GNU Affero General Public License v3.0](LICENSE). You're free to use, study, modify and share
TapQueue. If you run a modified version for others, including as a hosted service, you must
publish your changes under the same license.
