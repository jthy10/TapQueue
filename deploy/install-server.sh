#!/usr/bin/env bash
# Installs or upgrades tapqueue-server on this machine (Ubuntu/Debian with systemd).
# Run it from an extracted tapqueue-server-<version>-linux-x64.tar.gz:
#
#   sudo ./install-server.sh
#
# First install: creates the tapqueue user, /opt/tapqueue, /etc/tapqueue/server.toml (with a fresh
# admin token) and the systemd service. Upgrade: replaces the programs and restarts the service;
# the config and data are left alone.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
prefix=/opt/tapqueue
confdir=/etc/tapqueue
config=$confdir/server.toml
unit=/etc/systemd/system/tapqueue-server.service

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this as root: sudo $0" >&2
    exit 1
fi
for f in tapqueue-server tapqueue-admin libe_sqlite3.so server.example.toml tapqueue-server.service; do
    if [ ! -e "$here/$f" ]; then
        echo "$here/$f is missing. Run this from an extracted tapqueue-server release archive." >&2
        exit 1
    fi
done

new_version=$("$here/tapqueue-server" --version)
if [ -x "$prefix/tapqueue-server" ]; then
    echo "Upgrading $("$prefix/tapqueue-server" --version) -> $new_version"
else
    echo "Installing $new_version"
fi

if ! id tapqueue >/dev/null 2>&1; then
    useradd --system --home-dir /var/lib/tapqueue --shell /usr/sbin/nologin tapqueue
    echo "Created system user tapqueue"
fi

systemctl stop tapqueue-server 2>/dev/null || true
install -d -m 755 "$prefix"
install -m 755 "$here/tapqueue-server" "$here/tapqueue-admin" "$prefix/"
install -m 644 "$here/libe_sqlite3.so" "$prefix/"
ln -sf "$prefix/tapqueue-admin" /usr/local/bin/tapqueue-admin

install -d -m 755 "$confdir"
if [ ! -e "$config" ]; then
    token=$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')
    sed "s/^token = \"change-me\"/token = \"$token\"/" "$here/server.example.toml" > "$config"
    chown root:tapqueue "$config"
    chmod 640 "$config"
    echo "Wrote $config with a new admin token"
else
    echo "Keeping existing $config"
fi

install -m 644 "$here/tapqueue-server.service" "$unit"
systemctl daemon-reload
systemctl enable --now tapqueue-server

for _ in $(seq 1 20); do
    if curl -fsS http://127.0.0.1:"$(sed -n 's/^listen = ".*:\([0-9]*\)"/\1/p' "$config" | head -1)"/healthz >/dev/null 2>&1; then
        echo "tapqueue-server is running: $(systemctl is-active tapqueue-server)"
        break
    fi
    sleep 0.5
done
if ! systemctl is-active --quiet tapqueue-server; then
    echo "tapqueue-server didn't start. See: journalctl -u tapqueue-server -n 50" >&2
    exit 1
fi

cat <<NEXT

Next steps (tapqueue-admin run with sudo reads the admin token from $config):
  sudo tapqueue-admin queues add secure --name "TapQueue Secure Print"
  sudo tapqueue-admin printers add office ipp://<printer-ip>/ipp/print --name "Office printer"
  sudo tapqueue-admin users add <username> --name "Full Name"
  sudo tapqueue-admin status
Logs: journalctl -u tapqueue-server -f
NEXT
