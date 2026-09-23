#!/usr/bin/env bash
# Builds release archives into dist/.
#
#   scripts/package.sh server    server and station (they share <Version>):
#     TapQueue_server_<version>_linux-x64.tar.gz    tapqueue-server, tapqueue-admin, libe_sqlite3.so, example config,
#                                                   systemd units, install-server.sh
#     TapQueue_station_<version>_linux-x64.tar.gz   tapqueue-station, example config, systemd unit, udev rule,
#                                                   install-station.sh
#   scripts/package.sh client    the Windows client (<TapQueueClientVersion>):
#     TapQueue_client_<version>_win-x64.zip         TapQueueClient.exe and version.txt, for `tapqueue-admin clients publish`
#                                                   (the installer, TapQueue_client_<version>.exe, is built on Windows by
#                                                   scripts/package-client-installer.ps1)
#   scripts/package.sh           both
#
# Each run writes SHA256SUMS for what it built. Used by CI on every push and by the release workflow on tags.
set -euo pipefail
cd "$(dirname "$0")/.."

what=${1:-all}
case "$what" in server | client | all) ;; *) echo "Usage: $0 [server|client]" >&2; exit 2 ;; esac

prop() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" Directory.Build.props; }
version=$(prop Version)
client_version=$(prop TapQueueClientVersion)
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

tarball() { # dir name
    tar -czf "$dist/$2.tar.gz" --transform "s:^\.:$2:" -C "$out/$1" .
}

if [ "$what" != client ]; then
    publish TapQueue.Server linux-x64 server
    publish TapQueue.Admin linux-x64 server
    rm -f "$out/server/"*.staticwebassets.*   # ASP.NET build leftover; the server serves no static files
    cp config/server.example.toml deploy/systemd/tapqueue-server.service deploy/systemd/tapqueue-console.service \
        deploy/install-server.sh "$out/server/"
    tarball server "TapQueue_server_${version}_linux-x64"

    publish TapQueue.Station linux-x64 station
    cp config/station.example.toml deploy/systemd/tapqueue-station.service deploy/udev/60-tapqueue-pcprox.rules \
        deploy/install-station.sh "$out/station/"
    tarball station "TapQueue_station_${version}_linux-x64"
fi

if [ "$what" != server ]; then
    publish TapQueue.Client.Windows win-x64 client -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
    # `tapqueue-admin clients publish` reads this; it matches what the client reports (TapQueueVersion.Current).
    echo "$client_version+$(git rev-parse --short=7 HEAD)" > "$out/client/version.txt"
    (cd "$out/client" && zip -q "../../$dist/TapQueue_client_${client_version}_win-x64.zip" TapQueueClient.exe version.txt)
fi

(cd "$dist" && sha256sum -- * > SHA256SUMS)
ls -l "$dist"
