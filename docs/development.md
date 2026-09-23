# Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build          # builds everything, including the Windows client (on Linux too)
dotnet test
dotnet run --project src/TapQueue.Server -- --config path/to/server.toml
scripts/package.sh    # release archives into dist/
```

A minimal dev `server.toml`:

```toml
[server]
listen = "0.0.0.0:8631"
data_dir = "./data"

[auth]
mode = "dev"

[admin]
token = "dev-admin-token"
```

Then add a queue and a printer with `tapqueue-admin --token dev-admin-token queues add …` and `printers add …`.

## Tests

- `tests/TapQueue.Server.Tests` has unit tests (IPP encoding, stores, owner matching) and
  end-to-end tests under `Integration/`. Those start a real server on a random localhost port with
  its own data directory, plus a fake IPP printer, and then drive them over HTTP and IPP the way the
  tray app, stations and `tapqueue-admin` do. Use them for any change to printing, releasing or the APIs.
- `tests/TapQueue.Station.Tests` covers badge reader decoding.

The IPP endpoint is also checked against the CUPS conformance suites:

```sh
ipptool -t -f /usr/share/cups/data/testprint ipp://localhost:8631/ipp/secure ipp-1.1.test
ipptool -t -f /usr/share/cups/data/testprint ipp://localhost:8631/ipp/secure ipp-everywhere.test
```

`ipp-1.1.test` passes. `ipp-everywhere.test` has two known differences:
- The suite expects printer supply levels (toner and so on). A hold queue has no supplies, so it
  reports them as `unknown`.
- The suite looks for `overrides-supported = document-number`, but the spec keyword (and the one
  CUPS' own `ipp-2.2.test` checks for) is `document-numbers`, which is what we send.

## Database changes

The schema is a list of numbered migrations in
[`Database.cs`](../src/TapQueue.Server/Data/Database.cs). To change it, append a new migration.
Never edit one that has been released. The server applies pending migrations at startup, and it
refuses to run against a database from a newer version.

## Layout

```
src/TapQueue.Server/          ASP.NET Core server
  Ipp/                        IPP encoder/decoder, queue endpoint, printer client
  Jobs/                       spool, owner matching, release, cleanup
  Data/                       SQLite storage and migrations
  Printers/                   printer status monitoring
  Api/                        REST API for clients, stations and admins
src/TapQueue.Admin/           tapqueue-admin
src/TapQueue.Station/         tapqueue-station (badge reader service)
src/TapQueue.Client.Windows/  WinForms tray client
src/TapQueue.Shared/          API types, config loading and version shared by all of the above
tests/                        unit and end-to-end tests
config/                       example config files
deploy/                       systemd units, udev rule, install script
scripts/                      packaging
docs/                         documentation
```

## Commits and releases

Keep commits small and focused. Add user-visible changes to `CHANGELOG.md` under `[Unreleased]`
as you go. See [releasing](releasing.md).
