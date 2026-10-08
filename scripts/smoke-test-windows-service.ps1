# Installs TapQueueClient.exe as a real Windows service (as LocalSystem, like the installer does),
# starts it, and fails if it doesn't stay running, or can't tell which Windows user is asking on its
# pipe (what it vouches for tray apps with). Prints the service's event log entries either way.
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

    # A tray app asking the service to vouch for its session. With no server there's no key to vouch
    # with, but the service works out which Windows user is asking first, and that's what's tested:
    # it failed on real PCs while the service had only ever talked plain HTTP.
    $answer = $null
    if ($state -eq 'Running') {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'TapQueue', [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Identification)
        try {
            $pipe.Connect(10000)
            $writer = [System.IO.StreamWriter]::new($pipe)
            $writer.Write("verify-session`nsmoke-test`n")
            $writer.Flush()
            $answer = [System.IO.StreamReader]::new($pipe).ReadLine()
        }
        finally { $pipe.Dispose() }
        Write-Host "The service's answer to verify-session: $answer"
    }

    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in 'TapQueue', '.NET Runtime', 'Application Error' } |
        Sort-Object TimeCreated |
        ForEach-Object { Write-Host "--- $($_.TimeCreated) $($_.ProviderName)"; Write-Host $_.Message }

    if ($state -ne 'Running') { throw "The TapQueue service didn't stay running ($state)." }
    if ($answer -notmatch "hasn't got its TapQueue key") { throw "The TapQueue service couldn't tell which Windows user asked it to vouch: $answer" }
}
finally {
    sc.exe stop $name | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $name | Out-Null
}
