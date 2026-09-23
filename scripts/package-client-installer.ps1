# Builds dist/TapQueue_client_<version>.exe, the Windows client installer. Runs on Windows
# (Inno Setup is Windows-only); used by CI and the release workflow.
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

New-Item -ItemType Directory -Force dist | Out-Null
& $iscc /Qp "/DAppVersion=$version" "/DExeSource=$PWD\out\client\TapQueueClient.exe" "/DOutputDir=$PWD\dist" deploy/windows/tapqueue-client.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Get-ChildItem dist
