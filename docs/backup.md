# Backups and restoring

The server backs itself up every day and before every upgrade that changes its database. A backup
is one file, `tapqueue-<time>-<why>.tar.gz`, holding everything that can't be made again:

- the database: users, cards, groups, admins, printers, queues, stations, PCs, settings, page
  counts, job history and the activity log;
- the server's TLS certificate and key (`tls/`), so PCs and stations that trust this server still
  do after a restore;
- the Active Directory CA certificate (`directory-ca/`).

Not in it: **held documents** (jobs waiting to be released are gone in a day anyway),
**published client and station builds** (publish the current ones again), and
**`/etc/tapqueue/server.toml`** (keep a copy of that yourself; it holds `admin.token`).

A backup holds every hashed password and token and the server's private key. Treat one like the
server itself. Only full admins can list, make, download or delete them.

## When backups are made

| Why | When | Kept |
|---|---|---|
| `daily` | Every day at 02:00 on the server's clock. If the server is off then, when it next starts. | The newest 7 |
| `upgrade` | When a newer server starts and is about to change the database. The older version can still read this copy. | The newest 3 |
| `manual` | When an admin asks: **Server → Backups → Back up now**, or `tapqueue-admin backups create`. | Until an admin deletes it |

Change the time, turn the daily backup off or keep more of them under **Server → Backups →
Schedule**, or:

```sh
tapqueue-admin backups                    # the schedule and every backup
tapqueue-admin backups schedule 23:30     # or: off
tapqueue-admin backups keep 30
tapqueue-admin backups create
tapqueue-admin backups delete <name>
```

The server keeps working while a backup is made. A failed daily backup is in the activity log
and shown on the Server page.

## Keep copies somewhere else

Backups are written to `/var/lib/tapqueue/backups`, on the server's own disk. That covers a bad
upgrade or a mistake; it doesn't cover the disk or the machine. Copy them off, either with
**Download** in the console or from another machine:

```sh
tapqueue-admin --server https://tapqueue.example.com:8632 --token <admin.token> \
    backups download latest /srv/backups/tapqueue/
```

`latest` is the newest backup; a name from `tapqueue-admin backups` fetches that one. Run it from
cron after the daily backup's time and that machine has a copy every day. The file is saved
readable only by you.

## Restoring

Restoring replaces the database, certificate and CA with the backup's. It's done with the server
stopped, by the server program itself, as the user the server runs as:

```sh
sudo systemctl stop tapqueue-server
sudo -u tapqueue /opt/tapqueue/tapqueue-server --restore /var/lib/tapqueue/backups/<name>
sudo systemctl start tapqueue-server
```

The `tapqueue` user has to be able to read the file: one in `/var/lib/tapqueue/backups` already
is; for one you copied over, put it in `/tmp` and `chmod 644` it, or `chown tapqueue` it.

What it does:

1. Checks the backup: that it is one, that its database isn't damaged, and that it isn't from a
   newer TapQueue than the one installed. If any of that fails, nothing is changed.
2. Moves what's there now into `/var/lib/tapqueue/replaced-<time>/`. Nothing is deleted; remove
   that folder once you're happy.
3. Puts the backup's files in place.
4. Cancels jobs that were waiting when the backup was made and whose documents aren't on this
   server; their owners print again. Forgets published builds whose files aren't on this server,
   so PCs and stations keep what they run until you publish again.

A backup from an older TapQueue restores fine: the server upgrades the database when it starts.

### Onto a new machine

1. Install the same or a newer TapQueue server ([install-server.md](install-server.md)) and put
   your `server.toml` back in `/etc/tapqueue`.
2. Restore the backup as above.
3. Give the machine the old one's name or address, or point PCs and stations at the new one.
   The certificate comes with the backup, so their `server_cert_fingerprint` still matches. A
   self-signed certificate lists the names it was made for; if the new machine has a different
   name, see [tls.md](tls.md).
4. Publish the current client and station builds again (**Updates → Check for updates**, or
   `tapqueue-admin clients update` and `tapqueue-admin stations update`).

### Undoing an upgrade

Stop the server, install the older release, and restore the `upgrade` backup made when the newer
one first started. Anything that happened since then is lost.
