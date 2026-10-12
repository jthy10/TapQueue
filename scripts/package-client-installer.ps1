# Builds the Windows client release into dist/. Runs on Windows (Inno Setup is Windows-only);
# used by CI and the release workflow.
#   TapQueue_client_<version>.exe            the installer (what people download and run)
#   TapQueue_client_<version>_win-x64.zip    TapQueueClient.exe + version.txt, for `tapqueue-admin clients publish`
#   SHA256SUMS
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.TapQueueClientVersion | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <TapQueueClientVersion> in Directory.Build.props' }

dotnet publish src/TapQueue.Client.Windows -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o out/client
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) {
    choco install innosetup -y --no-progress
    if ($LASTEXITCODE -ne 0) { throw 'Installing Inno Setup failed' }
}

Remove-Item -Recurse -Force dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force dist | Out-Null
& $iscc /Qp "/DAppVersion=$version" "/DExeSource=$PWD\out\client\TapQueueClient.exe" "/DOutputDir=$PWD\dist" deploy/windows/tapqueue-client.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

# What the client reports as its version (TapQueueVersion.Current); `tapqueue-admin clients publish` reads it.
"$version+$(git rev-parse --short=7 HEAD)" | Set-Content -NoNewline out/client/version.txt
Compress-Archive -Force -Path out/client/TapQueueClient.exe, out/client/version.txt -DestinationPath "dist/TapQueue_client_${version}_win-x64.zip"

Get-ChildItem dist -File | Where-Object Name -ne SHA256SUMS | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
} | Set-Content dist/SHA256SUMS
Get-ChildItem dist
