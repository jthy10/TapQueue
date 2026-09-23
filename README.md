# TapQueue

Open source print management with badge release. Users print to one shared queue,
and their jobs wait on the server until they walk up to any printer and release them there.
Nothing sits in the output tray for someone else to pick up.

> **Status: early development.** Print → hold → release works end to end against a real
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

| Component | Runs on | |
|---|---|---|
| **tapqueue-server** | Linux | IPP hold queue, REST API, releases jobs to printers |
| **tapqueue-admin** | Linux | Command-line admin tool |
| **tapqueue-station** | Linux | Release station: a USB badge reader next to a printer; a tap releases your jobs |
| **TapQueue client** | Windows 11 | Installer, tray app (signs you in, shows held jobs) and a service that adds the printer and installs updates pushed from the server |

Download them from [Releases](https://github.com/jthy10/TapQueue/releases): `TapQueue_client_X.Y.Z.exe`
for PCs (`client-v` releases), and the server and station (`server-v` releases), which install
with one command:

```sh
curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s server    # or: station
```

## How it works

1. **The server looks like an ordinary network printer.** Windows 11 prints to it with its
   built-in IPP driver, so there's nothing to install on the PC but the TapQueue client.
2. **Jobs are held, not printed.** The server keeps the document and the print options chosen.
3. **The job is matched to a person** through the tray app, which is signed in on the PC the
   job came from. The username inside a print job is easy to fake, so it isn't trusted on its own.
4. **The user releases it at a printer** by tapping their badge at the release station next to it,
   or from the tray app.
5. **Unreleased jobs expire** after 24 hours (configurable) and are deleted.

More in [how TapQueue works](docs/how-it-works.md), including what matching jobs to people by
IP address can't handle.

## Getting started

1. [Install the server](docs/install-server.md), then add a queue, your printers and users.
2. [Install the Windows client](docs/windows-client.md) on each PC.
3. [Set up a release station](docs/release-station.md) next to each printer and enroll badges.

Reference: [admin CLI](docs/admin-cli.md) · changelogs: [server and station](CHANGELOG.md),
[Windows client](CHANGELOG-client.md) ·
[development](docs/development.md) · [releasing](docs/releasing.md)

## Roadmap

- [x] IPP hold queue that Windows 11 can print to with no driver install
- [x] Hold jobs per user; release to a physical printer over IPP, keeping print options
- [x] Windows tray client configured by a file
- [x] Admin CLI
- [x] Release station: Linux service for a USB badge reader next to each printer
- [x] Badge enrollment by an admin (tap, then `badges add --last-tap`)
- [x] Queues and printers managed without restarting the server
- [x] Versioned releases with prebuilt archives and an install script
- [x] Windows installer; client updates pushed from the server
- [x] One-command server and station installs from GitHub
- [ ] Feedback at the printer (screen or beeper) for "nothing to print" and errors
- [ ] Self-service badge enrollment from the tray app
- [ ] TLS for client and IPP connections
- [ ] Web admin console
- [ ] Directory sync (Active Directory / LDAP / Entra ID)
- [ ] Page counting, quotas and reports
- [ ] Signed Windows builds; .deb packages; ARM builds for small release devices

## License

[GNU Affero General Public License v3.0](LICENSE). You're free to use, study, modify and share
TapQueue. If you run a modified version for others, including as a hosted service, you must
publish your changes under the same license.
