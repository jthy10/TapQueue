#!/usr/bin/env bash
# Installs or upgrades the TapQueue Linux client on this PC (Ubuntu/Debian desktop with systemd and CUPS).
# Run it from an extracted TapQueue_client_<version>_linux-x64.tar.gz, or let install.sh fetch it:
#
#   sudo ./install-client.sh              install or upgrade
#   sudo ./install-client.sh --uninstall  remove it, and the printers it added (keeps client.toml)
#
# First install: finds TapQueue servers on the network and asks which to use (set TAPQUEUE_SERVER
# to install without questions). Upgrade: replaces the program and restarts the service; the
# config is left alone.
#
# What goes where:
#   /opt/tapqueue-client/tapqueue-client        the program: a symlink to the current build in builds/, so
#                                               updates never replace a file a running tray app uses
#   /etc/tapqueue/client.toml                   settings for the PC
#   tapqueue-client.service                     adds the printers (CUPS) and installs updates, as root
#   /etc/xdg/autostart/tapqueue-client.desktop  starts the tray app when anyone signs in to the desktop
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
prefix=/opt/tapqueue-client
confdir=/etc/tapqueue
config=$confdir/client.toml
unit=/etc/systemd/system/tapqueue-client.service
desktop=tapqueue-client.desktop

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this as root: sudo $0 $*" >&2
    exit 1
fi

if [ "${1:-}" = --uninstall ]; then
    systemctl disable --now tapqueue-client 2>/dev/null || true
    if [ -x "$prefix/tapqueue-client" ]; then
        DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/tapqueue-client "$prefix/tapqueue-client" --remove-printers ||
            echo "Couldn't remove every TapQueue printer; see lpstat -v." >&2
    fi
    pkill -x tapqueue-client 2>/dev/null || true # tray apps
    rm -rf "$prefix" /var/lib/tapqueue-client /var/cache/tapqueue-client
    rm -f "$unit" "/etc/xdg/autostart/$desktop" "/usr/share/applications/$desktop"
    systemctl daemon-reload
    echo "Removed the TapQueue client. Kept $config, so reinstalling remembers the server."
    exit 0
fi

for f in tapqueue-client client.example.toml tapqueue-client.service "$desktop"; do
    if [ ! -e "$here/$f" ]; then
        echo "$here/$f is missing. Run this from an extracted TapQueue client release archive." >&2
        exit 1
    fi
done
if ! command -v lpadmin >/dev/null; then
    echo "Warning: CUPS isn't installed (no lpadmin), so TapQueue can't add its printers. Install it with: sudo apt install cups" >&2
fi

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

export DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/tapqueue-client
install -d -m 755 "$DOTNET_BUNDLE_EXTRACT_BASE_DIR"
new_version=$("$here/tapqueue-client" --version)
if [ -x "$prefix/tapqueue-client" ]; then
    old_version=$(timeout 5 "$prefix/tapqueue-client" --version 2>/dev/null) || old_version="an earlier version"
    echo "Upgrading $old_version -> $new_version"
else
    echo "Installing $new_version"
fi

install -d -m 755 "$confdir"
if [ ! -e "$config" ]; then
    server=${TAPQUEUE_SERVER:-}
    if [ -z "$server" ]; then
        echo "Looking for TapQueue servers on the network..."
        mapfile -t found < <("$here/tapqueue-client" --discover 2>/dev/null || true)
        default=http://tapqueue-server:8631
        if [ "${#found[@]}" -gt 0 ]; then
            for i in "${!found[@]}"; do
                IFS='|' read -r url name version <<<"${found[$i]}"
                echo "  $((i + 1))) $url  ($name, $version)"
            done
            default=${found[0]%%|*}
            pick=$(ask "TapQueue server (number or address)" "$default")
            if [[ "$pick" =~ ^[0-9]+$ ]] && [ "$pick" -ge 1 ] && [ "$pick" -le "${#found[@]}" ]; then
                pick=${found[$((pick - 1))]%%|*}
            fi
            server=$pick
        else
            echo "  none found (the search only reaches this PC's own network)"
            server=$(ask "TapQueue server address" "$default")
        fi
    fi
    sed -e "s|^server_url = .*|server_url = \"$server\"|" "$here/client.example.toml" > "$config"
    # Readable by everyone: each user's tray app reads it. A per-PC token would be visible to all
    # users of the PC, as it is on Windows; see docs/linux-client.md.
    chmod 644 "$config"
    echo "Wrote $config"
else
    echo "Keeping existing $config"
fi

systemctl stop tapqueue-client 2>/dev/null || true
install -d -m 755 "$prefix" "$prefix/builds"
build=tapqueue-client-$(sha256sum "$here/tapqueue-client" | cut -c1-16)
install -m 755 "$here/tapqueue-client" "$prefix/builds/$build"
ln -sfn "builds/$build" "$prefix/tapqueue-client.new"
mv -Tf "$prefix/tapqueue-client.new" "$prefix/tapqueue-client" # replaces the program file of a 0.5 pre-release too
rm -f "$prefix/tapqueue-client.old"
# Older builds are deleted by the service once no tray app runs them.
install -m 644 "$here/tapqueue-client.service" "$unit"
install -m 644 "$here/$desktop" /etc/xdg/autostart/
install -m 644 "$here/$desktop" /usr/share/applications/
systemctl daemon-reload
systemctl enable --now tapqueue-client
sleep 3
if systemctl is-active --quiet tapqueue-client; then
    echo "tapqueue-client is running:"
    journalctl -u tapqueue-client -n 3 --no-pager -o cat
else
    echo "tapqueue-client didn't start. See: journalctl -u tapqueue-client -n 50" >&2
    exit 1
fi

cat <<NEXT

The TapQueue printers appear within a minute (lpstat -v).
The tray app starts when someone signs in to the desktop; to start it now, open TapQueue from the
app menu. Tray apps already running restart into the new version by themselves.
Logs:  journalctl -u tapqueue-client -f
NEXT
