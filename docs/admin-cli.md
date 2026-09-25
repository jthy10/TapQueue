# tapqueue-admin

```
tapqueue-admin status                          Server version and a summary of what's set up

tapqueue-admin users                           List users
tapqueue-admin users add <username> [--name]   Create a user and print their client token
tapqueue-admin users reset-token <username>    Issue a new client token
tapqueue-admin users quota <username> [<pages> day|week|month | none]
                                               Show a user's page limits and usage, or set or remove their own
tapqueue-admin groups quota <id> <pages> day|week|month | none
                                               Set or remove a group's page limit
tapqueue-admin quotas                          Everyone with a page limit and how much they've used

tapqueue-admin queues                          List queues (the printers users see in Windows)
tapqueue-admin queues add <id> --name <name> [--description] [--location] [--color] [--duplex] [--media]
tapqueue-admin queues edit <id> [--name] [--description] [--location] [--color on|off] [--duplex on|off] [--media]
tapqueue-admin queues remove <id>

tapqueue-admin printers [--refresh]            Printers with their health and toner levels
tapqueue-admin printers add <id> <uri> [--name] [--location] [--tls-skip-verify]
tapqueue-admin printers edit <id> [--uri] [--name] [--location] [--tls-skip-verify on|off]
tapqueue-admin printers remove <id>

tapqueue-admin jobs [--status held]            List recent jobs
tapqueue-admin release <user> <printer> [ids]  Release a user's held jobs to a printer

tapqueue-admin badges [username]               List badges
tapqueue-admin badges add <user> <card>|--last-tap
                                               Link a badge to a user
tapqueue-admin badges unknown                  Unrecognized cards tapped in the last hour
tapqueue-admin badges remove <badge-id>        Unlink a badge

tapqueue-admin stations                        Release stations and when they last checked in
tapqueue-admin stations add <id> <printer>     Create a station and print its token
tapqueue-admin stations move <id> <printer>    Make a station release to another printer
tapqueue-admin stations reset-token <id>       Issue a new station token
tapqueue-admin stations remove <id>            Delete a station

tapqueue-admin clients                         Signed-in clients and their versions
tapqueue-admin clients publish <zip|tar.gz|program> [--platform win-x64|linux-x64] [--version-name]
                                               Push a client build to every PC of its platform
                                               (told from the program itself)
tapqueue-admin clients builds                  Published client builds
tapqueue-admin clients sign-out <session>      Sign a client out; jobs from that PC stop going to them

tapqueue-admin workstations                    PCs with the TapQueue service: client, update errors, who's signed in
tapqueue-admin workstations update <computer>  Install the published client now (retries a failed update)
tapqueue-admin workstations forget <computer>  Drop a PC that's gone
tapqueue-admin crashes [<computer>]            Crash reports sent by clients, newest first
tapqueue-admin crashes show <id>               One report with its full error
tapqueue-admin crashes clear [<computer>]      Delete crash reports (all, or one PC's)

tapqueue-admin directory                       Active Directory sync: settings, scope, next and recent syncs
tapqueue-admin directory sync [--dry-run] [--force]
                                               Sync now, only show what would change, or go past the disable limit
tapqueue-admin directory scope add <dn>        Sync the users under an OU, in a group, or one user
tapqueue-admin directory scope remove <id>     Stop syncing a scope item

tapqueue-admin server                          Settings, and whether each is set here or in server.toml
tapqueue-admin server set hold-hours|session-timeout <number|default>
                                               Change a setting now, or go back to server.toml's
tapqueue-admin server set quota-overrun allow|deny|default
                                               Whether a job may take someone over their page limit
tapqueue-admin server log [--follow]           Recent server log lines; --follow keeps printing new ones
tapqueue-admin server restart                  Restart tapqueue-server (only when systemd runs it)
```

`tapqueue-admin --help` shows every option.

## Page limits

A page limit is a number of pages per day, week (from Monday) or calendar month, counted in the
server's time zone (set it with `timedatectl set-timezone`). Pages are counted when a job is
released: its pages (after any page range the user picked) times copies. TapQueue counts pages
in PDF, PWG raster, Apple raster and JPEG documents; anything else counts as 1 page per copy and
shows `?` in `tapqueue-admin jobs`.

- A limit on a user overrides their groups'.
- Otherwise the limits of their groups apply, and a job prints if any of them allows it (the most
  generous wins). Groups without a limit don't lift anyone's.
- Someone with no limit on them or any of their groups can print as much as they like.

```sh
sudo tapqueue-admin groups quota students 200 month
sudo tapqueue-admin users quota asmith 500 month     # this person gets more
sudo tapqueue-admin quotas
```

What happens to a job that would go over is up to you (`server set quota-overrun`):

| Setting | A job prints when |
|---|---|
| `allow` (default) | the person is under their limit when it starts, even if it takes them over |
| `deny` | it fits in the pages they have left |

A job that can't be released stays held, and the station, client or console says why.

## Admins

Roles in the admin console, and local users' console passwords. Full admins only (the admin token
is one). See [admin-roles.md](admin-roles.md).

```
tapqueue-admin admins                                    # people, their roles, and every grant
tapqueue-admin admins grant alice admin                  # admin in every area: a full admin
tapqueue-admin admins grant @helpdesk operator --area jobs,fleet
tapqueue-admin admins revoke 3                           # grant id from `admins`
tapqueue-admin users password alice                      # asks for it twice
tapqueue-admin users password alice --clear
```

## Connecting

The first of these that's set wins:

1. `--server <url>` and `--token <token>`
2. `TAPQUEUE_SERVER` and `TAPQUEUE_ADMIN_TOKEN`
3. `~/.config/tapqueue/admin.toml`:
   ```toml
   server_url = "http://tapqueue-server:8631"
   token = "<admin.token from server.toml>"
   ```
4. `/etc/tapqueue/server.toml`, when it can be read (on the server, with `sudo`).

The admin token can do anything, so treat it like a root password. The API has no TLS yet (see the
roadmap), so use the admin CLI from the server itself or over a network you trust.
