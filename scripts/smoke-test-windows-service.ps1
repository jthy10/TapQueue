# Installs TapQueueClient.exe as a real Windows service (as LocalSystem, like the installer does),
# starts it, and fails if it doesn't stay running. Prints the service's event log entries either way.
# Used by CI on the Windows runner; needs admin rights.
#
#   ./scripts/smoke-test-windows-service.ps1 path\to\TapQueueClient.exe
param([Parameter(Mandatory)] [string] $Exe)
$ErrorActionPreference = 'Stop'

$exe = (Resolve-Path $Exe).Path
$name = 'TapQueueSmokeTest'
$config = Join-Path $env:RUNNER_TEMP 'smoke-client.toml'
# Nothing listens here: the service has to cope with an unreachable server.
Set-Content $config 'server_url = "http://127.0.0.1:9"'
$started = Get-Date

sc.exe create $name binPath= "`"$exe`" --service --config `"$config`"" start= demand | Out-Null
try {
    sc.exe start $name | Out-Null
    Start-Sleep -Seconds 20
    $state = (Get-Service $name).Status
    Write-Host "Service state after 20 seconds: $state"

    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in 'TapQueue', '.NET Runtime', 'Application Error' } |
        Sort-Object TimeCreated |
        ForEach-Object { Write-Host "--- $($_.TimeCreated) $($_.ProviderName)"; Write-Host $_.Message }

    if ($state -ne 'Running') { throw "The TapQueue service didn't stay running ($state)." }
}
finally {
    sc.exe stop $name | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $name | Out-Null
}
