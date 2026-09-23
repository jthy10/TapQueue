#!/usr/bin/env bash
# Builds release archives into dist/:
#   tapqueue-server-<version>-linux-x64.tar.gz    tapqueue-server, tapqueue-admin, libe_sqlite3.so, example config, systemd unit
#   tapqueue-station-<version>-linux-x64.tar.gz   tapqueue-station, example config, systemd unit, udev rule
#   tapqueue-client-<version>-win-x64.zip         TapQueueClient.exe, example config
#   SHA256SUMS
# Used by CI on every push and by the release workflow on tags.
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
out=out
dist=dist
rm -rf "$out" "$dist"
mkdir -p "$dist"

publish() { # project runtime dir [extra msbuild args...]
    local project=$1 runtime=$2 dir=$3
    shift 3
    dotnet publish "src/$project" -c Release -r "$runtime" --self-contained \
        -p:PublishSingleFile=true -p:DebugType=none -o "$out/$dir" "$@"
}

publish TapQueue.Server linux-x64 server
publish TapQueue.Admin linux-x64 server
rm -f "$out/server/"*.staticwebassets.*   # ASP.NET build leftover; the server serves no static files
cp config/server.example.toml deploy/systemd/tapqueue-server.service "$out/server/"

publish TapQueue.Station linux-x64 station
cp config/station.example.toml deploy/systemd/tapqueue-station.service deploy/udev/60-tapqueue-pcprox.rules "$out/station/"

publish TapQueue.Client.Windows win-x64 client -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
cp config/client.example.toml "$out/client/"

tar -czf "$dist/tapqueue-server-$version-linux-x64.tar.gz" --transform "s:^\.:tapqueue-server-$version:" -C "$out/server" .
tar -czf "$dist/tapqueue-station-$version-linux-x64.tar.gz" --transform "s:^\.:tapqueue-station-$version:" -C "$out/station" .
(cd "$out/client" && zip -qr "../../$dist/tapqueue-client-$version-win-x64.zip" .)
(cd "$dist" && sha256sum -- * > SHA256SUMS)

ls -l "$dist"
