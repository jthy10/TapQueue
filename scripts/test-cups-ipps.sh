#!/usr/bin/env bash
# Prints through CUPS to a tapqueue-server over ipps:// with its self-signed certificate, the way
# the Linux client adds printers (lpadmin -m everywhere), and checks the job is held.
# Needs root, CUPS (cups, cups-client) and a Release build: dotnet build -c Release
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
server=$root/src/TapQueue.Server/bin/Release/net10.0/tapqueue-server
admin=$root/src/TapQueue.Admin/bin/Release/net10.0/tapqueue-admin
work=$(mktemp -d)
trap 'kill "$pid" 2>/dev/null || true; lpadmin -x tq-ipps-test 2>/dev/null || true; rm -rf "$work"' EXIT

cat > "$work/server.toml" <<EOF
[server]
listen = "127.0.0.1:18631"
data_dir = "$work/data"
discovery_port = 0
[auth]
mode = "dev"
[admin]
token = "cups-test-token"
[tls]
listen = "127.0.0.1:18632"
EOF

"$server" --config "$work/server.toml" > "$work/server.log" 2>&1 &
pid=$!
for _ in $(seq 1 40); do
    curl -fsS --noproxy '*' http://127.0.0.1:18631/healthz >/dev/null 2>&1 && break
    sleep 0.5
done
export NO_PROXY='*'
"$admin" --server http://127.0.0.1:18631 --token cups-test-token queues add secure --name "TapQueue Secure Print"

ipptool -t ipps://127.0.0.1:18632/ipp/secure get-printer-attributes.test
lpadmin -p tq-ipps-test -E -v ipps://127.0.0.1:18632/ipp/secure -m everywhere
echo "TapQueue over ipps" > "$work/page.txt"
# Jobs nobody is signed in for are refused, so sign in from this machine as a tray app would.
curl -fsS --noproxy '*' -H 'Content-Type: application/json' -d '{"username":"cups-test","hostname":"ci","platform":"linux-x64"}' \
    http://127.0.0.1:18631/api/v1/client/session >/dev/null
lp -d tq-ipps-test -t "over ipps" "$work/page.txt"

for _ in $(seq 1 60); do
    if "$admin" --server http://127.0.0.1:18631 --token cups-test-token jobs | grep -q "over ipps"; then
        echo "CUPS printed over ipps:// with a self-signed certificate; the job is held."
        exit 0
    fi
    sleep 1
done
echo "The job never reached the server." >&2
lpstat -l -p tq-ipps-test >&2 || true
cat "$work/server.log" >&2
exit 1
