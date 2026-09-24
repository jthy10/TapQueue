# End to end test of a pushed update on Windows: installs TapQueueClient.exe (-From) as a real
# service with the installer's recovery settings, has a stand-in TapQueue server offer a different
# build (-To), and checks that the service installs it, restarts, and checks in running it.
# Used by CI on the Windows runner; needs admin rights.
#
#   ./scripts/test-windows-service-update.ps1 -From old\TapQueueClient.exe -To new\TapQueueClient.exe
param([Parameter(Mandatory)] [string] $From, [Parameter(Mandatory)] [string] $To)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:RUNNER_TEMP 'tapqueue-update-test'
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dir | Out-Null
$exe = Join-Path $dir 'TapQueueClient.exe'
Copy-Item $From $exe
$new = (Resolve-Path $To).Path
$oldSha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$newSha = (Get-FileHash $new -Algorithm SHA256).Hash.ToLower()
if ($oldSha -eq $newSha) { throw '-From and -To are the same build.' }
$port = 18631
$config = Join-Path $dir 'client.toml'
Set-Content $config "server_url = `"http://127.0.0.1:$port`"`ninstall_printers = false"
$checkIns = Join-Path $dir 'check-ins.txt'

# The stand-in server: offers the new build at every check-in, serves it, and records each
# check-in's reported SHA-256.
$server = Start-Job -ArgumentList $port, $new, $newSha, $checkIns -ScriptBlock {
    param($port, $new, $newSha, $checkIns)
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$port/")
    $listener.Start()
    $size = (Get-Item $new).Length
    $setup = @{ queues = @(); command = $null; clientBuild = @{
        version = '9.9.9+test'; sha256 = $newSha; sizeBytes = $size; publishedAt = '2026-01-01T00:00:00Z'
        downloadPath = "/api/v1/client/builds/$newSha"; platform = 'win-x64' } } | ConvertTo-Json -Depth 5
    while ($true) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response
        if ($request.HttpMethod -eq 'POST' -and $request.Url.AbsolutePath -eq '/api/v1/client/setup') {
            $body = [System.IO.StreamReader]::new($request.InputStream).ReadToEnd()
            Add-Content $checkIns (($body | ConvertFrom-Json).sha256)
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($setup)
            $response.ContentType = 'application/json'
        }
        elseif ($request.Url.AbsolutePath -eq "/api/v1/client/builds/$newSha") {
            $bytes = [System.IO.File]::ReadAllBytes($new)
        }
        else {
            $response.StatusCode = 404
            $bytes = @()
        }
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
        $response.Close()
    }
}

$name = 'TapQueueUpdateTest'
$started = Get-Date
sc.exe create $name binPath= "`"$exe`" --service --config `"$config`"" start= demand | Out-Null
# The same recovery settings as deploy/windows/tapqueue-client.iss.
sc.exe failure $name reset= 86400 actions= restart/2000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag $name 1 | Out-Null
try {
    sc.exe start $name | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $checkIns) -and (Get-Content $checkIns) -contains $newSha) { break }
        Start-Sleep -Seconds 2
    }
    Write-Host "Check-ins (SHA-256 reported):"
    if (Test-Path $checkIns) { Get-Content $checkIns | ForEach-Object { Write-Host "  $_" } }
    Write-Host "Service state: $((Get-Service $name).Status)"
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in 'TapQueue', '.NET Runtime', 'Application Error' } |
        Sort-Object TimeCreated |
        ForEach-Object { Write-Host "--- $($_.TimeCreated) $($_.ProviderName)"; Write-Host $_.Message }

    if (-not ((Test-Path $checkIns) -and (Get-Content $checkIns) -contains $newSha)) {
        throw "The service didn't come back running the new build within 90 seconds."
    }
    Write-Host "Updated: the service restarted and checked in running the new build."
}
finally {
    sc.exe stop $name | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $name | Out-Null
    Stop-Job $server; Remove-Job $server -Force
}
