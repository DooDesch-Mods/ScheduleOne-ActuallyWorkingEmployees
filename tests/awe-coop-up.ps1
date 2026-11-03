# Brings up a joined co-op pair for tests/awe-coop.ps1: the host on a fresh save, the client through LocalLobby's --join,
# which joins the lobby id duo.ps1 put in the client's lobby.txt. Side Hustle's self-test join takes the first lobby it
# finds, and that can be another session's.
#
#     pwsh -File tests/awe-coop-up.ps1
#
# Prints the host and client slot names on the last line ("pair: host=<slot> client=<slot>") for awe-coop.ps1.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$worktree = Split-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) -Leaf
$duo = Join-Path $root 'Workspace\tools\session\duo.ps1'

& pwsh -NoProfile -File $duo -Stop
& pwsh -File $duo -Build $worktree -BootSeconds 240
if ($LASTEXITCODE -ne 0) { Write-Host "duo failed with $LASTEXITCODE"; exit 1 }

$lease = (& python (Join-Path $root 'Workspace\tools\slots\slots.py') --json mine | ConvertFrom-Json).lease
$hostName = @($lease.slots)[0]
$name = @($lease.slots)[-1]
$client = $lease.table.$name
Write-Host "client slot: $name port $($client.port) root $($client.root)"
$clientRoot = $client.root
$exe = Join-Path $clientRoot 'Schedule I.exe'

Get-CimInstance Win32_Process -Filter "Name='Schedule I.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($clientRoot, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Host "stopped client pid $($_.ProcessId)" }
Start-Sleep -Seconds 4

$process = Start-Process -FilePath $exe -ArgumentList '--join' -WorkingDirectory $clientRoot -PassThru
$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if ($process.HasExited) { Write-Host "client exited with $($process.ExitCode)"; exit 1 }
    try {
        $h = Invoke-RestMethod -Uri "http://127.0.0.1:$($client.port)/health" -TimeoutSec 3
        Write-Host ("{0:HH:mm:ss} scene={1} inGame={2} loading={3}" -f (Get-Date), $h.scene, $h.inGame, $h.loading)
        if ($h.scene -eq 'Main' -and $h.inGame -and -not $h.loading) {
            Write-Host 'client is in the world'
            Write-Host "pair: host=$hostName client=$name"
            exit 0
        }
    } catch { Write-Host ("{0:HH:mm:ss} bridge down: {1}" -f (Get-Date), $_.Exception.Message) }
}
Write-Host 'client not in the world after 300 s'
exit 1
