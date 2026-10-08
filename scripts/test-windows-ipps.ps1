# End to end test of printing over https:// from Windows to a server with a self-signed
# certificate: runs tapqueue-server with TLS, installs TapQueueClient.exe as a real service pointed
# at https://localhost, and checks that the service trusts the certificate (it ends up in the
# machine's trusted roots), adds the printer with the built-in IPP driver, that a page printed to
# it is held, and that --remove-printers takes the printer and the certificate away again.
# Used by CI on the Windows runner; needs admin rights and a Release build (dotnet build -c Release).
#
#   ./scripts/test-windows-ipps.ps1 path\to\TapQueueClient.exe
param([Parameter(Mandatory)] [string] $Exe)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$serverExe = Join-Path $root 'src\TapQueue.Server\bin\Release\net10.0\tapqueue-server.exe'
$adminExe = Join-Path $root 'src\TapQueue.Admin\bin\Release\net10.0\tapqueue-admin.exe'
$exe = (Resolve-Path $Exe).Path
$dir = Join-Path $env:RUNNER_TEMP 'tapqueue-ipps-test'
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dir | Out-Null
$data = (Join-Path $dir 'data').Replace('\', '/')
$token = 'ipps-test-token'
$printer = 'TapQueue Secure Print'

Set-Content (Join-Path $dir 'server.toml') @"
[server]
listen = "127.0.0.1:18631"
data_dir = "$data"
discovery_port = 0
[admin]
token = "$token"
[tls]
listen = "127.0.0.1:18632"
"@
$server = Start-Process $serverExe -ArgumentList '--config', (Join-Path $dir 'server.toml') -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $dir 'server.log') -RedirectStandardError (Join-Path $dir 'server.err')
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    try { Invoke-RestMethod http://127.0.0.1:18631/healthz | Out-Null; break } catch { Start-Sleep -Seconds 1 }
}
function Admin { & $adminExe --server http://127.0.0.1:18631 --token $token @args; if ($LASTEXITCODE) { throw "tapqueue-admin $args failed" } }
Admin queues add secure --name $printer
$fingerprint = ((Admin server) | Select-String 'SHA-256\s+(\S+)').Matches[0].Groups[1].Value
$thumbprint = ([System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $dir 'data\tls\server.crt'))).Thumbprint
Write-Host "Server certificate SHA-256 $fingerprint, thumbprint $thumbprint"

$config = Join-Path $dir 'client.toml'
Set-Content $config "server_url = `"https://localhost:18632`"`nusername = `"ipps-test`""
$name = 'TapQueueIppsTest'
$started = Get-Date
sc.exe create $name binPath= "`"$exe`" --service --config `"$config`"" start= demand | Out-Null
$failure = $null
try {
    sc.exe start $name | Out-Null
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not (Get-Printer -Name $printer -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }

    if (-not (Test-Path "Cert:\LocalMachine\Root\$thumbprint")) { $failure = "The server's certificate isn't in the machine's trusted roots." }
    elseif (-not (Get-Printer -Name $printer -ErrorAction SilentlyContinue)) { $failure = "The service didn't add `"$printer`" within 120 seconds." }
    else {
        Get-Printer -Name $printer | Format-List Name, DriverName, PortName | Out-String | Write-Host
        'TapQueue over https' | Out-Printer -Name $printer
        $deadline = (Get-Date).AddSeconds(90)
        $held = $false
        while ((Get-Date) -lt $deadline -and -not $held) {
            Start-Sleep -Seconds 3
            $held = [bool]((Admin jobs) | Select-String 'held')
        }
        Admin jobs | Write-Host
        if (-not $held) {
            Get-PrintJob -PrinterName $printer -ErrorAction SilentlyContinue | Format-List | Out-String | Write-Host
            $failure = 'The page printed over https never reached the server.'
        }
    }
}
finally {
    sc.exe stop $name | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $name | Out-Null
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in 'TapQueue', '.NET Runtime', 'Application Error' } |
        Sort-Object TimeCreated |
        ForEach-Object { Write-Host "--- $($_.TimeCreated) $($_.ProviderName)"; Write-Host $_.Message }
}

Start-Process $exe -ArgumentList '--remove-printers' -Wait
if (Get-Printer -Name $printer -ErrorAction SilentlyContinue) { $failure = $failure ?? '--remove-printers left the printer behind.' }
if (Test-Path "Cert:\LocalMachine\Root\$thumbprint") { $failure = $failure ?? '--remove-printers left the certificate in the trusted roots.' }
Stop-Process $server -ErrorAction SilentlyContinue
if ($failure) {
    Get-Content (Join-Path $dir 'server.log'), (Join-Path $dir 'server.err') -ErrorAction SilentlyContinue | Write-Host
    throw $failure
}
Write-Host 'Windows printed over https:// to a self-signed server, and uninstalling cleaned up.'
