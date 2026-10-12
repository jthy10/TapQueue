# Backups and restoring

The server backs itself up every day and before every upgrade that changes its database. A backup
is one file, `tapqueue-<time>-<why>.tar.gz` (`.tar.gz.enc` when [encrypted](#encrypting-backups)),
holding everything that can't be made again:

- the database: users, cards, groups, admins, printers, queues, stations, PCs, settings, page
  counts, job history and the activity log;
- the server's TLS certificate and key (`tls/`), so PCs and stations that trust this server still
  do after a restore;
- the Active Directory CA certificate (`directory-ca/`);
- the key card numbers are hashed with (`secrets/`): without it no stored card can be matched.

Not in it: **held documents** (jobs waiting to be released are gone in a day anyway),
**published client and station builds** (publish the current ones again), and
**`/etc/tapqueue/server.toml`** (keep a copy of that yourself; it holds `admin.token`).

A backup holds every hashed password and token, the badge key and the server's private key. Treat
one like the server itself, and [encrypt them](#encrypting-backups) if they leave it. Only full
admins can list, make, download or delete them.

## When backups are made

| Why | When | Kept |
|---|---|---|
| `daily` | Every day at 02:00 in the server's time zone (**Server → Settings**; the machine's own unless set). If the server is off then, when it next starts. | The newest 7 |
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
upgrade or a mistake; it doesn't cover the disk or the machine. There are two ways to get them off.

**Have the server copy them.** Mount another disk or a network share (NFS, SMB) on the server and
name a folder on it in `server.toml`:

```toml
[backup]
copy_to = "/mnt/nas/tapqueue"
```

The service may only write under `/var/lib/tapqueue`, so let it write there too, and make the
folder the `tapqueue` user's:

```sh
sudo install -d -o tapqueue -g tapqueue -m 700 /mnt/nas/tapqueue
sudo systemctl edit tapqueue-server      # add the two lines below, save
#   [Service]
#   ReadWritePaths=/mnt/nas/tapqueue
sudo systemctl restart tapqueue-server
```

Every backup (daily, manual and before an upgrade) is then copied there as it's made. The folder
follows the server's: a backup deleted on the server, by hand or because it's older than what's
kept, is deleted there too. If the share is away the backup is still made; the Server page and
`tapqueue-admin backups` say the copy failed, it's in the activity log, and backups made
meanwhile are copied when the next one is. A share is only as private as its other users:
encrypt what goes there.

**Or fetch them from another machine**, with **Download** in the console or:

```sh
tapqueue-admin --server https://tapqueue.example.com:8632 --token <admin.token> \
    backups download latest /srv/backups/tapqueue/
```

`latest` is the newest backup; a name from `tapqueue-admin backups` fetches that one. Run it from
cron after the daily backup's time and that machine has a copy every day. The file is saved
readable only by you.

## Encrypting backups

```toml
[backup]
passphrase = "a long sentence nobody would guess"    # at least 12 characters
```

After a restart, every new backup is encrypted with a key made from the passphrase (AES-256-GCM;
PBKDF2-SHA-256, 600,000 rounds) and named `.tar.gz.enc`. Without the passphrase such a file gives
away nothing but its size, and a changed or cut-short one is refused rather than restored.
Backups made before stay as they were.

**Restoring needs the same passphrase, and there's no way round it.** `server.toml` isn't in the
backup, so keep the passphrase (and a copy of `server.toml`) somewhere that isn't the server: a
password manager, a sealed envelope. Changing the passphrase doesn't re-encrypt old backups;
restore one of those with the passphrase it was made with (`--passphrase-file`, below).

## Restoring

Restoring replaces the database, certificate, badge key and CA with the backup's. It's done with the server
stopped, by the server program itself, as the user the server runs as:

```sh
sudo systemctl stop tapqueue-server
sudo -u tapqueue /opt/tapqueue/tapqueue-server --restore /var/lib/tapqueue/backups/<name>
sudo systemctl start tapqueue-server
```

The `tapqueue` user has to be able to read the file: one in `/var/lib/tapqueue/backups` already
is; for one you copied over, put it in `/tmp` and `chmod 644` it, or `chown tapqueue` it.

An encrypted backup is decrypted with `backup.passphrase` from `server.toml`. For one made with
another passphrase, put that in a file only you can read and add `--passphrase-file <file>`:

```sh
sudo -u tapqueue /opt/tapqueue/tapqueue-server --restore <name>.tar.gz.enc --passphrase-file /run/tapqueue-passphrase
```

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
   your `server.toml` back in `/etc/tapqueue` (with `backup.passphrase`, if the backup is encrypted).
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
