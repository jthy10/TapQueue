# TapQueue

Open source print management with badge release. Users print to one shared queue,
and their jobs wait on the server until they walk up to any printer and release them there.
Nothing sits in the output tray for someone else to pick up.

> **Status: early development.** Print → hold → release works end to end against a real
> printer, from the tray app, a web page or by tapping a badge at a release station. Expect
> breaking changes.

```
 Windows 11 / Linux PC             TapQueue server (Linux)                 At the printer
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
| **TapQueue client** | Windows 11, Linux desktops | Tray app (shows held jobs, releases them) and a service that adds the printer, says who is signed in to the PC and installs updates pushed from the server |
| **Web pages** | Any browser | The admin console at `/admin`, and `/my`, where people see and release their own jobs from a phone |

Download them from [Releases](https://github.com/jthy10/TapQueue/releases): `TapQueue_client_X.Y.Z.exe`
for Windows PCs (`client-v` releases); the server, station and Linux client install with one command:

```sh
curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s server    # or: station, client
```

## How it works

1. **The server looks like an ordinary network printer.** Windows 11 prints to it with its
   built-in IPP driver and Linux with CUPS's driverless IPP Everywhere support, so there's nothing
   to install on the PC but the TapQueue client.
2. **Jobs are held, not printed.** The server keeps the document and the print options chosen.
3. **The job is matched to a person** by the PC it came from. Each PC's printers carry a key, and
   the PC's TapQueue service says which PC user printed, so this works on terminal servers and
   behind NAT too, with nothing for anyone to type. The username inside a print job is easy to
   fake, so it isn't trusted on its own.
4. **The user releases it at a printer** by tapping their badge at the release station next to it,
   from the tray app, or from the web page on their phone.
5. **Unreleased jobs expire** after 24 hours (configurable) and are deleted.
6. **It's counted.** Pages by person, group and printer, page limits, and if you want it a price
   per page that comes off a balance.

More in [how TapQueue works](docs/how-it-works.md), including how jobs are matched to people.

## Getting started

1. [Install the server](docs/install-server.md), create the first admin with the setup code it
   prints, then add a queue, your printers and users.
2. Install the client on each PC: [Windows](docs/windows-client.md) or [Linux](docs/linux-client.md).
3. [Set up a release station](docs/release-station.md) next to each printer and enroll badges.

Reference: [admin console](docs/admin-ui.md) · [TLS](docs/tls.md) · [backups](docs/backup.md) · [reports](docs/reports.md) · [cost accounting and balances](docs/accounting.md) · [the web page for people's own jobs](docs/web-page.md) · [admin sign-in and roles](docs/admin-roles.md) · [admin CLI](docs/admin-cli.md) · [Active Directory sync](docs/active-directory.md) · [single sign-on and Entra ID](docs/single-sign-on.md) · changelogs: [server and station](CHANGELOG.md),
[clients](CHANGELOG-client.md) ·
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
- [x] Web admin console
- [x] Page counting and quotas
- [x] Printer health monitoring (state, problems, toner levels)
- [x] Admin sign-in and roles, with single sign-on (OpenID Connect)
- [x] Active Directory and Entra ID sync
- [x] TLS for client and IPP connections
- [x] Per-job identity instead of IP-based ownership
- [x] Feedback at the printer: a screen for "nothing to print" and errors (a status light later)
- [x] Reports: pages and cost by person, group, printer and period, with CSV export
- [x] Follow-me printing: one queue, released at whichever printer you tap at
- [x] Cost accounting: a price per page, and balances people pay from
- [x] Self-service badge enrollment from the tray app and the web page
- [x] A web page to see and release your own jobs from a phone
- [x] Queue rules (always two-sided, black and white only, a longest job) and queues that print straight through
- [x] Sign-in with nothing to type: the PC's service says who is signed in to it
- [x] First-run setup in the console, without opening it to the network
- [x] Signed releases, so PCs and stations update without reaching GitHub
- [x] Database backups and restore
- [x] Email notifications for printer problems, offline stations and crashes (webhooks later)
- [ ] A Docker image; .deb packages; ARM builds for small release devices; signed Windows builds
- [ ] Scheduled allowances, shared accounts, and rules by group on one queue

The full list, with what each item means, is in [docs/roadmap.md](docs/roadmap.md).

## License

[GNU Affero General Public License v3.0](LICENSE). You're free to use, study, modify and share
TapQueue. If you run a modified version for others, including as a hosted service, you must
publish your changes under the same license.
