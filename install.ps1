# Installs, upgrades or repairs the TapQueue Windows client from the newest GitHub release.
# In PowerShell run as administrator:
#
#   irm https://raw.githubusercontent.com/jthy10/TapQueue/main/install.ps1 | iex
#
# A first install finds the TapQueue server on the network; to name it (or move the PC to another):
#
#   $env:TAPQUEUE_SERVER = "http://tapqueue-server:8631"; irm https://raw.githubusercontent.com/jthy10/TapQueue/main/install.ps1 | iex
#
# $env:TAPQUEUE_VERSION = "0.6.0" installs that release instead of the newest. Before installing it
# prints what state the client is in (service, version, server address, whether the server answers),
# which is what to send along if something is wrong. Written for Windows PowerShell 5.1.

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue" # the progress bar makes Invoke-WebRequest very slow
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = "jthy10/TapQueue"
$exe = Join-Path $env:ProgramFiles "TapQueue\TapQueueClient.exe"
$config = Join-Path $env:ProgramData "TapQueue\client.toml"

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Host "Run this in PowerShell as administrator (right-click Start, Terminal (Admin))." -ForegroundColor Red
    return
}

# What's there now.
Step "Current state"
$service = Get-Service TapQueue -ErrorAction SilentlyContinue
if ($service) { Write-Host "  Service: $($service.Status), starts $($service.StartType)" } else { Write-Host "  Service: not installed" }
if (Test-Path $exe) { Write-Host "  Program: $((Get-Item $exe).VersionInfo.ProductVersion), written $((Get-Item $exe).LastWriteTime)" } else { Write-Host "  Program: not installed" }
$tray = @(Get-Process TapQueueClient -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 })
Write-Host "  Tray apps running: $($tray.Count)"
$serverUrl = $null
if (Test-Path $config) {
    $line = Select-String -Path $config -Pattern '^\s*server_url\s*=\s*"([^"]*)"' | Select-Object -First 1
    if ($line) { $serverUrl = $line.Matches[0].Groups[1].Value }
}
Write-Host "  Server in client.toml: $(if ($serverUrl) { $serverUrl } else { '(none)' })"
$checkUrl = if ($env:TAPQUEUE_SERVER) { $env:TAPQUEUE_SERVER } else { $serverUrl }
if ($checkUrl) {
    try {
        $answer = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 -Uri ($checkUrl.TrimEnd('/') + "/")
        Write-Host "  $checkUrl answers: $($answer.Content.Trim())"
    } catch {
        Write-Host "  $checkUrl doesn't answer: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
$since = (Get-Date).AddDays(-2)
$errors = @(Get-WinEvent -FilterHashtable @{ LogName = "Application"; Level = 2; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match "TapQueue" } | Select-Object -First 3)
$errors += @(Get-WinEvent -FilterHashtable @{ LogName = "System"; ProviderName = "Service Control Manager"; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match "TapQueue" } | Select-Object -First 3)
foreach ($e in $errors) { Write-Host "  $($e.TimeCreated): $(($e.Message -split "`n")[0])" -ForegroundColor Yellow }

# The newest client-vX.Y.Z release (server releases share the repository).
Step "Finding the client release"
if ($env:TAPQUEUE_VERSION) {
    $tag = "client-v$($env:TAPQUEUE_VERSION)"
} else {
    $releases = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$repo/releases?per_page=100"
    $tag = ($releases | Where-Object { $_.tag_name -like "client-v*" -and -not $_.draft -and -not $_.prerelease } | Select-Object -First 1).tag_name
    if (-not $tag) { throw "No client release found on GitHub." }
}
$version = $tag.Substring("client-v".Length)
$installerName = "TapQueue_client_$version.exe"
$base = "https://github.com/$repo/releases/download/$tag"
Write-Host "  $installerName"

$work = Join-Path $env:TEMP "tapqueue-install-$version"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$installer = Join-Path $work $installerName
Step "Downloading"
Invoke-WebRequest -UseBasicParsing -Uri "$base/$installerName" -OutFile $installer
$sumsFile = Join-Path $work "SHA256SUMS"
Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS" -OutFile $sumsFile
$expected = (Get-Content $sumsFile | Where-Object { $_ -match [regex]::Escape($installerName) + '\s*$' } | Select-Object -First 1) -replace '\s.*$', ''
$actual = (Get-FileHash -Algorithm SHA256 $installer).Hash.ToLowerInvariant()
if (-not $expected -or $expected.ToLowerInvariant() -ne $actual) { throw "Checksum mismatch for $installerName (expected $expected, got $actual)." }
Write-Host "  Checksum OK"

# Setup keeps the server address already in client.toml unless one is given.
Step "Installing TapQueue $version"
$log = Join-Path $work "setup.log"
$arguments = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$log`"")
if ($env:TAPQUEUE_SERVER) { $arguments += "/SERVER=$($env:TAPQUEUE_SERVER)" }
$setup = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
if ($setup.ExitCode -ne 0) {
    Get-Content $log -Tail 15 -ErrorAction SilentlyContinue
    throw "Setup failed with exit code $($setup.ExitCode). Full log: $log"
}

Step "Starting"
$service = Get-Service TapQueue
if ($service.Status -ne "Running") { Start-Service TapQueue }
# Through Explorer, so the tray runs as the signed-in user rather than elevated.
if (-not (Get-Process TapQueueClient -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId })) {
    Start-Process explorer.exe -ArgumentList "`"$exe`""
}
Write-Host "  Service: $((Get-Service TapQueue).Status); program $((Get-Item $exe).VersionInfo.ProductVersion)"
Write-Host "TapQueue $version is installed. Other people on this PC get the tray app next time they sign in." -ForegroundColor Green
