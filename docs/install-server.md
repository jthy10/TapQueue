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

The server listens on port 8631 for both IPP (printing) and its REST API. Open it in the firewall
if you use one: `sudo ufw allow 8631/tcp`. It also answers the Windows installer's search for
servers on UDP port 8631 (`sudo ufw allow 8631/udp`); with only TCP open, the installer still
finds servers on its own subnet, just more slowly.

## Set up queues, printers and users

Run `tapqueue-admin` with `sudo` on the server and it reads the admin token from
`/etc/tapqueue/server.toml`. From another machine, see [admin CLI](admin-cli.md#connecting).

```sh
# The printer users see in Windows. They print to ipp://<server>:8631/ipp/secure
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
| `server.listen` | `0.0.0.0:8631` | Address and port for IPP and the API |
| `server.data_dir` | `/var/lib/tapqueue` | Database and held jobs |
| `server.discovery_port` | `8631` | UDP port the Windows installer's server search is answered on. `0` = off |
| `auth.mode` | `token` | `token`: users need the token from `users add`, and the admin console needs a sign-in. `dev`: a username is enough, and the console is open to anyone (setup and testing only; see [admin-roles.md](admin-roles.md)) |
| `auth.session_timeout_minutes` | `10` | Client sessions without a heartbeat for this long end |
| `admin.token` | | Used by `tapqueue-admin`; gives full admin access |
| `jobs.hold_hours` | `24` | Held jobs not released within this time are deleted |

Queues, printers, users, badges and stations live in the database and are managed with
`tapqueue-admin`. Changes to them take effect immediately. Changes to `server.toml` need
`sudo systemctl restart tapqueue-server` (or Restart on the console's Server page).

`auth.session_timeout_minutes` and `jobs.hold_hours` are only defaults: they can be changed while
the server runs from the console's Server page or with `tapqueue-admin server set`, and those
values are kept in the database until they're set back to `default`.

## Upgrading

Run the same one-line install again (or extract the new release and run its `install-server.sh`).
It replaces the programs, keeps your config and data, and restarts the service. Database changes are applied automatically at startup.
Read the [changelog](../CHANGELOG.md) first: before 1.0, minor versions can need manual steps.

## Backups

Everything is in `/var/lib/tapqueue` (`tapqueue.db`, plus `spool/` with the held jobs) and
`/etc/tapqueue/server.toml`. To back up the database while the server runs:

```sh
sudo sqlite3 /var/lib/tapqueue/tapqueue.db ".backup /root/tapqueue-$(date +%F).db"
```

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
