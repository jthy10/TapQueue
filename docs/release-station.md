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

On the station, extract `tapqueue-station-X.Y.Z-linux-x64.tar.gz` from
[Releases](https://github.com/jthy10/TapQueue/releases) and:

```sh
cd tapqueue-station-X.Y.Z
sudo useradd --system --no-create-home --shell /usr/sbin/nologin --groups input tapqueue-station
sudo install -d /opt/tapqueue /etc/tapqueue
sudo install -m 755 tapqueue-station /opt/tapqueue/

/opt/tapqueue/tapqueue-station --list-devices                    # find the reader
sudo /opt/tapqueue/tapqueue-station --test --device /dev/input/by-id/usb-…-event-kbd
                                                                 # tap a card; its number is printed
sudo /opt/tapqueue/tapqueue-station --test --reader pcprox       # same, for a pcProx
sudo install -m 644 60-tapqueue-pcprox.rules /etc/udev/rules.d/  # pcProx only
sudo udevadm control --reload-rules && sudo udevadm trigger

sudo install -m 640 -g tapqueue-station station.example.toml /etc/tapqueue/station.toml
sudoedit /etc/tapqueue/station.toml          # server_url, token, reader, device
sudo install -m 644 tapqueue-station.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now tapqueue-station
journalctl -u tapqueue-station -f            # "Station "lobby" releases to Office printer (online)."
```

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
