# Installing the server

`tapqueue-server` runs on any x86_64 Linux with systemd. It's tested on Ubuntu 24.04 and 26.04.
It needs very little: a few hundred MB of RAM, and disk space for held jobs (they're deleted
when they're released or expire).

Give it a fixed address (a static IP or DHCP reservation, ideally with a DNS name). Every Windows
client and release station points at it.

## One command (recommended)

```sh
curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s server
```

This downloads the newest server release from [Releases](https://github.com/jthy10/TapQueue/releases),
checks it against `SHA256SUMS`, and runs the `install-server.sh` inside it. Add a version to get a
specific one: `... | sudo bash -s server 0.3.0`.

To do the same by hand, download `TapQueue_server_X.Y.Z_linux-x64.tar.gz`, then:

```sh
tar xzf TapQueue_server_X.Y.Z_linux-x64.tar.gz
sudo ./TapQueue_server_X.Y.Z_linux-x64/install-server.sh
```

The script:
- creates a `tapqueue` system user;
- installs the programs to `/opt/tapqueue` and links `tapqueue-admin` into `/usr/local/bin`;
- writes `/etc/tapqueue/server.toml` with a freshly generated admin token (first install only);
- installs and starts the `tapqueue-server` systemd service.

The server listens on port 8632 for IPP (printing), its REST API and the admin console over TLS,
with a self-signed certificate it makes on first start, and on port 8631 for the same without
encryption. See [TLS](tls.md) for using your own certificate and turning plain HTTP off. Open both
in the firewall if you use one: `sudo ufw allow 8631:8632/tcp`. It also answers the Windows installer's search for
servers on UDP port 8631 (`sudo ufw allow 8631/udp`); with only TCP open, the installer still
finds servers on its own subnet, just more slowly.

## Set up queues, printers and users

Run `tapqueue-admin` with `sudo` on the server and it reads the admin token from
`/etc/tapqueue/server.toml`. From another machine, see [admin CLI](admin-cli.md#connecting).

```sh
# The printer users see in Windows. They print to ipps://<server>:8632/ipp/secure
sudo tapqueue-admin queues add secure --name "TapQueue Secure Print"

# Each physical printer jobs can be released to
sudo tapqueue-admin printers add office ipp://192.0.2.10/ipp/print --name "Office printer"

# Each user; the command prints the token for their client.toml
sudo tapqueue-admin users add jsmith --name "Jane Smith"

sudo tapqueue-admin status
```

Most network printers accept IPP at `ipp://<printer-ip>/ipp/print`. `tapqueue-admin printers`
shows whether the server can reach each one. To check a printer by hand:
`ipptool -tv ipp://<printer-ip>/ipp/print get-printer-attributes.test`.

Then set up the [Windows client](windows-client.md) and a [release station](release-station.md).

## Configuration

`/etc/tapqueue/server.toml` holds settings for the server itself. See
[`config/server.example.toml`](../config/server.example.toml):

| Setting | Default | |
|---|---|---|
| `server.listen` | `0.0.0.0:8631` | Address and port for IPP and the API without TLS |
| `tls.listen` | `0.0.0.0:8632` | The same over TLS (HTTPS and IPPS). `""` = off. More `tls.` settings in [tls.md](tls.md#settings) |
| `server.data_dir` | `/var/lib/tapqueue` | Database and held jobs |
| `server.discovery_port` | `8631` | UDP port the Windows installer's server search is answered on. `0` = off |
| `auth.mode` | `token` | `token`: users need the token from `users add`, and the admin console needs a sign-in. `dev`: a username is enough, and the console is open to anyone (setup and testing only; see [admin-roles.md](admin-roles.md)) |
| `auth.session_timeout_minutes` | `10` | Client sessions without a heartbeat for this long end |
| `admin.token` | | Used by `tapqueue-admin`; gives full admin access |
| `jobs.hold_hours` | `24` | Held jobs not released within this time are deleted |
| `server.trusted_proxies` | `[]` | Reverse proxies whose `X-Forwarded-For` is believed; see [below](#behind-a-reverse-proxy) |
| `backup.copy_to` | `""` | A second folder (another disk, a mounted share) every backup is copied to; see [backup.md](backup.md#keep-copies-somewhere-else) |
| `backup.passphrase` | `""` | Encrypts backups; see [backup.md](backup.md#encrypting-backups) |

Queues, printers, users, badges and stations live in the database and are managed with
`tapqueue-admin`. Changes to them take effect immediately. Changes to `server.toml` need
`sudo systemctl restart tapqueue-server` (or Restart on the console's Server page).

### Changing them in the console

Most of these don't need the file edited. A full admin can change them on the console's
**Server** page, under **Startup settings**: `auth.mode`, everything under `tls.`,
`server.discovery_port`, `server.trusted_proxies`, `backup.copy_to` and `backup.passphrase`. A
change is checked the way the file is at startup (and a certificate file is loaded, a new HTTPS
port tried), saved in the database, and used from the next restart, which the page offers. Until
then the page shows both what's running and what's saved.

A value saved there wins over `server.toml`'s. Only values that differ from the file's are kept,
so a setting nobody changed in the console keeps following the file, and **Go back to
server.toml's** forgets them all. They're in the database, so they're in backups and come back
with a restore.

`server.listen`, `server.data_dir` and `admin.token` are only ever read from the file: the
database is in `data_dir`, and `tapqueue-admin` and the install scripts find the server on its
own machine by the other two.

If a setting saved in the console keeps the server from starting (a certificate file that's
since been deleted, a port something else took), the console can't be used to fix it. The
server's error in `journalctl -u tapqueue-server` names what was set there; forget them and
start with the file alone:

```sh
sudo -u tapqueue /opt/tapqueue/tapqueue-server --forget-console-settings
sudo systemctl restart tapqueue-server
```

`auth.session_timeout_minutes` and `jobs.hold_hours` are only defaults: they can be changed while
the server runs from the console's Server page or with `tapqueue-admin server set`, and those
values are kept in the database until they're set back to `default`.

More settings live only there (**Server → Settings**, `tapqueue-admin server`), each with a
default that suits a small office:

| Setting | Default | |
|---|---|---|
| `time-zone` | the server machine's | Where days start: page limit periods, reports, and the times of the daily backup and directory sync. An IANA name like `Europe/Oslo`. A server whose clock is on UTC resets "daily" limits at UTC midnight until you set this |
| `job-history` | `0` (for ever) | Days finished jobs' history (document names, owners, source PCs) is kept. `0`, or 32 and up; reports reach back only as far |
| `activity-log` | `90` | Days the activity log is kept. `0` = for ever |
| `tap-wait` | `5` | Seconds a tap at a station waits for the printer before the station reads the next card ([release-station.md](release-station.md)) |
| `printer-busy` | `120` | Seconds a printer that answers "busy" is retried before the job is held again |
| `printer-send` | `300` | Seconds sending one job to a printer may take |
| `sign-in-attempts` | `5` | Wrong passwords for a name from one address before sign-in is refused there ([admin-roles.md](admin-roles.md)) |
| `sign-in-lockout` | `15` | Minutes it stays refused |

### Behind a reverse proxy

TapQueue serves HTTPS itself ([tls.md](tls.md)) and doesn't need a proxy. If you put one in front
anyway (nginx, Caddy, a load balancer), every connection reaches the server from the proxy's
address. Unless told otherwise the server takes that as the caller's: five wrong passwords from
anyone lock everyone out, jobs without a PC key are matched to the wrong people, and the activity
log shows one address for everybody.

So tell it which proxies to believe:

```toml
[server]
trusted_proxies = ["10.0.0.5"]        # addresses, or networks like "10.0.0.0/24"
```

For connections from those addresses, and only those, the caller's address comes from
`X-Forwarded-For` (the last entry that isn't itself a trusted proxy) and whether they used HTTPS
from `X-Forwarded-Proto`. The proxy has to set both: nginx's `proxy_set_header X-Forwarded-For
$proxy_add_x_forwarded_for;` and `proxy_set_header X-Forwarded-Proto $scheme;`, and Caddy does
by itself. The headers are ignored from everyone else, since anyone can send them. Things to get
right:

- List only the proxy. Anything on a trusted address can claim to be any caller.
- Make the server reachable only through the proxy (a firewall, or `server.listen =
  "127.0.0.1:8631"` when the proxy is on the same machine). Trusting `127.0.0.1` also trusts
  every program on the server machine.
- Print jobs are IPP over HTTP POST with bodies up to 512 MB: raise the proxy's body size limit
  and turn off request buffering for `/ipp/`.
- `tapqueue-admin server` and the console's Server page show which proxies are trusted.

## Upgrading

Read the [changelog](../CHANGELOG.md) first: before 1.0, minor versions can need manual steps.

**From the admin console:** on the Server page, **Check for updates** asks GitHub for the newest
server release. If it's newer than what's running, choose to update now or at a time you pick
(within 30 days; you can call a scheduled update off). The server doesn't run as root, so it can't
replace itself: it writes the version to `/var/lib/tapqueue/update-request`, and
`tapqueue-update.path` starts `tapqueue-update.service`, which runs `/opt/tapqueue/update-server.sh`
as root. That script downloads the release from GitHub, checks it against the release's
`SHA256SUMS`, refuses anything that isn't newer than what's installed, and runs the archive's
`install-server.sh`, the same as the one-line install. The console shows how it went; the details are
in `journalctl -u tapqueue-update`. Checking needs the server to reach `api.github.com` and
`github.com`; nothing is checked until an admin asks.

Servers installed before 0.8.0 don't have `tapqueue-update` yet: upgrade them once by hand.

**By hand:** run the same one-line install again (or extract the new release and run its
`install-server.sh`). It replaces the programs, keeps your config and data, and restarts the
service. Database changes are applied automatically at startup.

## Downgrading

When an update turns out badly, the Server page's **Downgrade** section takes the whole site back
to an older release: **Look for older releases** lists them, and **Go back to X.Y.Z** does it
(`tapqueue-admin server downgrade` lists them, `tapqueue-admin server downgrade X.Y.Z` goes back).

- **Everything goes back together.** The server first hands out that release's clients and station
  build, which every PC and station installs within about a minute, then installs that release of
  the server and restarts. Only releases that have all of those under one version are listed.
- **0.17.0 is as far back as it goes.** It's the first release that can be gone back to; nothing
  older is ever listed.
- **The database is stepped back first.** An older server can't read a database a newer one has
  changed. So before the older server is installed, the one that's running undoes its own
  database changes, back to what the older one needs. Whatever only the newer version kept track
  of is dropped. A backup is made first (**Before a downgrade** under Backups, kept until you
  delete it); held jobs, users, badges and settings are kept.
- Like upgrading, it needs a server installed by `install-server.sh` and run by systemd, and it
  has to reach GitHub. If the database can't be stepped back, nothing is installed and the server
  starts again as it was. Details are in `journalctl -u tapqueue-update`.

A PC or station that's switched off catches up when it's next on: it installs whatever build the
server hands out then.

## Backups

Everything is in `/var/lib/tapqueue` (`tapqueue.db`, plus `spool/` with the held jobs) and
`/etc/tapqueue/server.toml`. The server backs up its database, certificate and Active Directory CA
into `/var/lib/tapqueue/backups` every day at 02:00 and before an upgrade changes the database.
Copy them somewhere else, and keep a copy of `server.toml` too:

```sh
tapqueue-admin backups                         # what there is, and the schedule
tapqueue-admin backups download latest /mnt/nas/tapqueue/
```

Restoring, moving to a new machine and undoing an upgrade are in [backup.md](backup.md).

## Logs

```sh
journalctl -u tapqueue-server -f
```

At startup the server logs its version, each queue and whether each printer is reachable.

To show the live log on the server's own screen instead of a login prompt (log in with Alt+F2),
install the optional `tapqueue-console` service from
[`deploy/systemd/tapqueue-console.service`](../deploy/systemd/tapqueue-console.service); the
commands to enable and undo it are at the top of that file.

## Building from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then:

```sh
git clone https://github.com/jthy10/TapQueue.git && cd TapQueue
scripts/package.sh     # builds the same archives as a release into dist/
```
