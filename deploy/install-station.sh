#!/usr/bin/env bash
# Installs or upgrades tapqueue-station on this machine (Ubuntu/Debian with systemd).
# Run it from an extracted TapQueue_station_<version>_linux-x64.tar.gz, or let install.sh fetch it:
#
#   sudo ./install-station.sh
#
# First install: asks for the server address and the station token (from
# `sudo tapqueue-admin stations add <station-id> <printer-id>` on the server), finds the badge
# reader, and starts the service. Set TAPQUEUE_SERVER, TAPQUEUE_STATION_TOKEN and optionally
# TAPQUEUE_READER_DEVICE to install without questions.
# Upgrade: replaces the program and restarts the service; the config is left alone.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
prefix=/opt/tapqueue
confdir=/etc/tapqueue
config=$confdir/station.toml
unit=/etc/systemd/system/tapqueue-station.service
user=tapqueue-station

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this as root: sudo $0" >&2
    exit 1
fi
for f in tapqueue-station station.example.toml tapqueue-station.service 60-tapqueue-pcprox.rules; do
    if [ ! -e "$here/$f" ]; then
        echo "$here/$f is missing. Run this from an extracted TapQueue station release archive." >&2
        exit 1
    fi
done

# Questions go to the terminal even when this script is piped into bash.
ask() { # prompt [default]
    local answer
    if [ ! -r /dev/tty ]; then
        echo "$2"
        return
    fi
    read -r -p "$1${2:+ [$2]}: " answer </dev/tty
    echo "${answer:-$2}"
}

new_version=$("$here/tapqueue-station" --version)
if [ -x "$prefix/tapqueue-station" ]; then
    # Builds from before --version existed would start the service instead, so don't wait long.
    old_version=$(timeout 5 "$prefix/tapqueue-station" --version 2>/dev/null) || old_version="an earlier version"
    echo "Upgrading $old_version -> $new_version"
else
    echo "Installing $new_version"
fi

if ! id "$user" >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin --groups input "$user"
    echo "Created system user $user"
fi

systemctl stop tapqueue-station 2>/dev/null || true
install -d -m 755 "$prefix"
install -m 755 "$here/tapqueue-station" "$prefix/"
install -m 644 "$here/60-tapqueue-pcprox.rules" /etc/udev/rules.d/
udevadm control --reload-rules && udevadm trigger --subsystem-match=hidraw || true

install -d -m 755 "$confdir"
if [ ! -e "$config" ]; then
    server=${TAPQUEUE_SERVER:-$(ask "TapQueue server address" "http://tapqueue-server:8631")}
    token=${TAPQUEUE_STATION_TOKEN:-$(ask "Station token (from 'sudo tapqueue-admin stations add <station-id> <printer-id>' on the server)" "")}

    # RFIDeas pcProx: USB vendor 0c27. Anything else is treated as a keyboard-style reader.
    reader=keyboard
    device=${TAPQUEUE_READER_DEVICE:-}
    if grep -qsi '^0c27$' /sys/bus/usb/devices/*/idVendor; then
        reader=pcprox
        device=""
        echo "Found an RFIDeas pcProx badge reader."
    elif [ -z "$device" ]; then
        mapfile -t keyboards < <(ls /dev/input/by-id/*-event-kbd 2>/dev/null || true)
        if [ "${#keyboards[@]}" -gt 0 ]; then
            echo "Keyboard-style devices (one of them should be the badge reader):"
            for i in "${!keyboards[@]}"; do echo "  $((i + 1))) ${keyboards[$i]}"; done
            pick=$(ask "Which one is the badge reader? (number, empty = set it later)" "")
            if [[ "$pick" =~ ^[0-9]+$ ]] && [ "$pick" -ge 1 ] && [ "$pick" -le "${#keyboards[@]}" ]; then
                device=${keyboards[$((pick - 1))]}
            fi
        fi
    fi

    sed -e "s|^server_url = .*|server_url = \"$server\"|" \
        -e "s|^token = .*|token = \"$token\"|" \
        -e "s|^reader = .*|reader = \"$reader\"|" \
        -e "s|^device = .*|device = \"$device\"|" \
        "$here/station.example.toml" > "$config"
    chown root:"$user" "$config"
    chmod 640 "$config"
    echo "Wrote $config"
    if [ -z "$token" ] || { [ "$reader" = keyboard ] && [ -z "$device" ]; }; then
        echo "Finish it with: sudoedit $config   (token and/or device are empty), then: sudo systemctl restart tapqueue-station"
    fi
else
    echo "Keeping existing $config"
fi

install -m 644 "$here/tapqueue-station.service" "$unit"
systemctl daemon-reload
systemctl enable --now tapqueue-station
sleep 3
if systemctl is-active --quiet tapqueue-station; then
    echo "tapqueue-station is running:"
    journalctl -u tapqueue-station -n 3 --no-pager -o cat
else
    echo "tapqueue-station didn't start. See: journalctl -u tapqueue-station -n 50" >&2
    exit 1
fi

cat <<NEXT

Test the reader:   sudo systemctl stop tapqueue-station && sudo $prefix/tapqueue-station --test && sudo systemctl start tapqueue-station
Enroll a badge:    tap it here, then on the server: sudo tapqueue-admin badges add <username> --last-tap
Logs:              journalctl -u tapqueue-station -f
NEXT
