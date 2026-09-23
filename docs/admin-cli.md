# tapqueue-admin

```
tapqueue-admin status                          Server version and a summary of what's set up

tapqueue-admin users                           List users
tapqueue-admin users add <username> [--name]   Create a user and print their client token
tapqueue-admin users reset-token <username>    Issue a new client token

tapqueue-admin queues                          List queues (the printers users see in Windows)
tapqueue-admin queues add <id> --name <name> [--description] [--location] [--color] [--duplex] [--media]
tapqueue-admin queues edit <id> [--name] [--description] [--location] [--color on|off] [--duplex on|off] [--media]
tapqueue-admin queues remove <id>

tapqueue-admin printers [--refresh]            Printers and whether they're reachable
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
```

`tapqueue-admin --help` shows every option.

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
