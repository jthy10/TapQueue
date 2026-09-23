# Release stations

A release station is a small Linux box next to a printer with a USB badge reader plugged in. A
badge tap sends all of that person's held jobs to the station's printer. The station never
touches documents and doesn't need to reach the printer. It only needs to reach the server.

Tested on a Dell OptiPlex 7060 Micro with Ubuntu 26.04. Any x86_64 Linux with systemd will do.

## Badge readers

- **Keyboard-style readers** (`reader = "keyboard"`, most USB readers) "type" the card number and
  press Enter. The station reads the reader directly from `/dev/input` and grabs it, so card
  numbers don't end up typed into a login prompt.
- **RFIDeas pcProx** (`reader = "pcprox"`) is polled over its HID feature-report channel instead,
  which also works when the reader is set to "SDK mode" and types nothing. Based on
  [ID-Card-Reader](https://github.com/jthy10/ID-Card-Reader), whose notes explain the reader's quirks.

## Install

On the server, create the station and choose the printer it releases to:

```sh
sudo tapqueue-admin stations add lobby office     # prints the station token
```

On the station:

```sh
curl -fsSL https://raw.githubusercontent.com/jthy10/TapQueue/main/install.sh | sudo bash -s station
```

It downloads the newest station release, asks for the server address and the station token,
finds the badge reader (a pcProx is detected automatically; for other readers it lists the
keyboard-style devices to pick from), and starts the `tapqueue-station` service. For unattended
installs, set `TAPQUEUE_SERVER`, `TAPQUEUE_STATION_TOKEN` and `TAPQUEUE_READER_DEVICE`
(`... | sudo TAPQUEUE_SERVER=... bash -s station`). Run it again to upgrade; the config is kept.

Check the reader and logs:

```sh
sudo systemctl stop tapqueue-station
/opt/tapqueue/tapqueue-station --list-devices                    # find the reader
sudo /opt/tapqueue/tapqueue-station --test                       # tap a card; its number is printed
sudo systemctl start tapqueue-station
journalctl -u tapqueue-station -f            # "Station "lobby" releases to Office printer (online)."
```

The settings are in `/etc/tapqueue/station.toml` (`sudoedit` it, then
`sudo systemctl restart tapqueue-station`).

To move a station to another printer, change it on the server. Nothing changes on the station:
`sudo tapqueue-admin stations move lobby front-desk`.

## Enrolling badges

Tap a new card at any station. The station reports it as unrecognized. Then link it:

```sh
sudo tapqueue-admin badges add jsmith --last-tap  # links the card most recently tapped anywhere
sudo tapqueue-admin badges add jsmith 04A1B2C3    # or type the number if you already know it
```

A tap then sends all of that user's held jobs to the station's printer. If a job can't be sent,
it stays held so the user can try again at another printer.

> Card numbers from cheap 125 kHz and MIFARE readers are easy to copy, just like a building badge.
> TapQueue treats a tap as "this person is standing at the printer", nothing stronger.

## Managing stations from the server

Once a station has its server address and token, everything else can be set from the admin
console's Stations page (or `PATCH /api/v1/admin/stations/<id>`), and overrides station.toml:
the printer it releases to, the reader and device, the repeat time, the minimum card length, its
name and location. A station sends a heartbeat every 15 seconds and picks up changes with the
reply; changing the reader makes it restart itself. The console shows each station's version,
reader status and when it last reported in; after 45 seconds without a heartbeat it shows as
offline.

A station can be **taken out of service** with a message: taps then release nothing and the
message is logged at the station instead. **Restart** asks it to restart on its next heartbeat.

### Updates

Publish a station build from the console's Updates page (the `tapqueue-station` program from the
station archive). Every station downloads it, checks it runs (`--version`), installs it into
`/var/lib/tapqueue-station` and restarts, within about 15 seconds. The service starts from there
when a build has been installed that way. Running `install-station.sh` again removes it, so a
build installed by hand always takes over.
