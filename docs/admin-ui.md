# Admin console

The admin console is a web page served by `tapqueue-server` at `http://<server>:8631/admin`. It
covers everything `tapqueue-admin` does, and adds the things a CLI is bad at: live status,
tap-to-enroll, logs, and remote control of stations and clients.

> **Development only, for now.** The console has no sign-in yet. It's only served when
> `auth.mode = "dev"`, and while it is, the admin API also accepts requests without the admin
> token. With `auth.mode = "token"` the console is off and the API needs the token as before.

## Principles

- **One API.** The console is a client of `/api/v1/admin`, the same API the CLI uses. Nothing
  the console can do is out of the CLI's reach, and every feature is built and tested at the
  API first.
- **No build step.** Plain HTML, CSS and ES modules in `src/TapQueue.Server/Admin/wwwroot`,
  embedded in the server binary. No npm, no bundler, no framework. A page is one module.
- **Explain, don't just refuse.** Errors say what to do next ("Move station office first"), the
  same way the CLI does. Destructive actions confirm and say what they'll affect.
- **Status is color; everything else is calm.** Green, amber and red only mean online, needs
  attention and failed.

## Layout

A sidebar groups pages by what you're doing; each page is a list with a detail drawer.

| Group | Page | What it's for |
|---|---|---|
| **Operate** | Overview | Health at a glance: held jobs, printers, stations and clients online, things that need attention, recent activity. |
| | Jobs | Held jobs and history. Filter by user, queue, status. Release to a printer, cancel. |
| | Activity | The audit trail: who changed what, badge taps, releases, sign-ins. |
| **People** | Users | Create, edit, enable/disable, reset token. A user's detail shows their cards, groups, jobs, sessions and quota. |
| | Groups | Groups of users, which queues and printers they may use, and their quotas. |
| | Cards | Every badge. Enroll by tapping at a station, reassign, label, remove. Unknown taps. |
| **Fleet** | Printers | Add, edit, remove, test. Live status, supported formats. |
| | Queues | The printers users see in Windows: name, defaults, who may print to them. |
| | Stations | Release stations: status, printer, reader, settings, restart, update, token. |
| | Workstations | Windows PCs running the client: who's signed in, client version, update now, sign out. |
| **System** | Updates | Client and station builds: publish, see who runs what, roll back. |
| | Server | Version, uptime, settings, live log, restart. |
| | Admins | Admin accounts and roles (for when sign-in exists). |

URLs are `/admin/<page>` and `/admin/<page>/<id>` for an open detail drawer, so every view can be
linked to and survives a reload.

## Code layout

```
src/TapQueue.Server/Admin/
  AdminUi.cs            serves wwwroot (embedded; from disk with TAPQUEUE_ADMIN_UI_DIR)
  wwwroot/
    index.html          the shell: sidebar, header, <main>
    app.js              router: path -> page module
    api.js              fetch wrapper; turns ErrorResponse into thrown errors
    ui.js               small helpers: h() to build elements, table, drawer, dialog, toast, format
    styles.css          design tokens (light + dark) and components
    pages/<page>.js     one module per page, exporting render(main, params)
```

For development, `TAPQUEUE_ADMIN_UI_DIR=src/TapQueue.Server/Admin/wwwroot` makes the server read
the files from disk, so edits show up on reload without a rebuild.

## Roadmap

Each phase ships on its own and leaves the console usable.

1. **Shell and what exists today.** *(Done.)* Overview, Jobs, Users, Cards, Printers, Queues, Stations,
   Workstations, Updates (client builds), Server (status).
2. **People.** *(Done.)* Enable/disable users, edit display names, card labels and reassigning, enrolling
   by tapping, cancel/release any job, delete users. An `events` table records every admin
   change, release and tap, which feeds Activity and the Overview.
3. **Groups and permissions.** *(Done, with CSV import/export and bulk actions.)* Groups, membership, and which queues and printers a group may
   use, checked when a job is printed (rejected with a clear IPP error) and when it's released
   (skipped, and the station says why). Users with no group can use everything, so nothing
   changes until groups are set up.
4. **Station control.** *(Done.)* Stations send a heartbeat every 15 s (version, reader state,
   uptime) and get settings, commands (restart) and the published build back. Every setting
   except the server address and token can be set from the console, and a station can be taken
   out of service with a message. Station builds are published and installed like client builds.
   Not done: tap feedback. The pcProx's LED and beeper are only reachable through the
   configuration commands that stop it reading cards, sound would need ALSA installed on every
   station, and the HP M404n ignores PJL panel messages. A screen or USB status light at the
   station is the likely way; `feedback` in station settings is reserved for it.
5. **Workstation and server control.** *(Done.)* The TapQueue service on each PC checks in once
   a minute, so Workstations lists PCs with their client version, a failed update's error and
   who's signed in. Sign a person out of a PC (the tray app stays signed out and their jobs stop
   going to them), tell a PC to update now, forget a PC. The Server page edits hold hours and
   session timeout (saved in the database; server.toml gives the defaults), streams the live log
   (server-sent events, the last 2000 lines kept in memory) and restarts the server when systemd
   runs it (exit code 75, which the unit restarts).
6. **Quotas.** Page counting for PDF and PWG raster jobs, page limits per user and group, per
   period, enforced at release.
7. **Admin sign-in and roles.** Admin accounts with roles (Admin, Operator, Viewer), sign-in,
   and the console available with `auth.mode = "token"`.
