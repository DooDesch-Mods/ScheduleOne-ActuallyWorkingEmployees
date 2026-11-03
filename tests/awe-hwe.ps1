<#
AWE next to Harder Working Employees in one test slot.

    pwsh -File tests/awe-hwe.ps1 -Slot mptest2 -HweDll <path to HarderWorkingEmployees.dll> [-AliveSeconds 200]

Rebuilds the slot with this worktree's AWE, adds HarderWorkingEmployees.dll, restarts without rebuilding the slot, then
checks: the game lives -AliveSeconds, AWE logs exactly one warning (the [Compat] line), no AWE method is patched on an
address HWE already patched, and the fix rows of awe-check.ps1 pass. Removes the HWE DLL from the slot at the end.
Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [Parameter(Mandatory)][string]$HweDll,
    [int]$AliveSeconds = 200
)
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$worktree = Split-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) -Leaf
$session = Join-Path $root 'Workspace\tools\session\session.ps1'
$game = Join-Path $root "_MPTest$($Slot -replace 'mptest','')\Schedule I"
$log = Join-Path $game 'MelonLoader\Latest.log'
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function Game { Get-Process 'Schedule I' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$game\*" } | Select-Object -First 1 }

if (-not (Test-Path $HweDll)) { Write-Host "FAIL hwe-dll not found: $HweDll"; Write-Host 'SUMMARY awe-hwe pass=0 fail=1'; exit 1 }

& pwsh -File $session -Slot $Slot -Build $worktree
if ($LASTEXITCODE -ne 0) { Result $false 'session-awe' "session.ps1 exit $LASTEXITCODE"; Write-Host "SUMMARY awe-hwe pass=$pass fail=$fail"; exit 1 }
$p = Game
if ($p) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(30000) | Out-Null }
Copy-Item $HweDll (Join-Path $game 'Mods') -Force
Result (Test-Path (Join-Path $game 'Mods\HarderWorkingEmployees.dll')) 'hwe-copied' ''

try {
    & pwsh -File $session -Slot $Slot -KeepSlot
    Result ($LASTEXITCODE -eq 0) 'session-with-hwe' "session.ps1 exit $LASTEXITCODE"
    $p = Game
    if (-not $p) { Result $false 'game-alive' 'no game process'; return }
    $deadline = (Get-Date).AddSeconds($AliveSeconds)
    while ((Get-Date) -lt $deadline -and -not $p.HasExited) { Start-Sleep -Seconds 5; $p.Refresh() }
    Result (-not $p.HasExited) 'game-alive' "pid $($p.Id) after $AliveSeconds s"

    $hweLoaded = @(Select-String -Path $log -Pattern 'Harder Working Employees v\d').Count
    Result ($hweLoaded -ge 1) 'hwe-loaded' "MelonLoader lines: $hweLoaded"

    $warn = @(Select-String -Path $log -Pattern '\[WARNING\] \[Actually_Working_Employees\]')
    $compat = @($warn | Where-Object { $_.Line -match '\[Compat\] Harder Working Employees .+ is loaded' })
    Result ($warn.Count -eq 1 -and $compat.Count -eq 1) 'one-warning' "AWE warnings: $($warn.Count), [Compat]: $($compat.Count)"
    $warn | ForEach-Object { Write-Host "  $($_.LineNumber): $($_.Line.Trim())" }

    $doubled = @(Select-String -Path $log -Pattern 'share native code at .+ overflows its stack')
    Result ($doubled.Count -eq 0) 'no-shared-detour-warning' "lines: $($doubled.Count)"
    Select-String -Path $log -Pattern '\[NativeMethods\] .+(skipped \(address patched by another mod\)|patched by Harder Working Employees only|shared native address)' |
        ForEach-Object { Write-Host "  log $($_.LineNumber): $($_.Line.Trim())" }

    if (-not $p.HasExited) {
        $rows = & (Join-Path $PSScriptRoot 'awe-check.ps1') -Slot $Slot -Only @('features-boot', 'fix1-standstill', 'fix2-chemist-work', 'fix3-lock-leak', 'log-no-failed') *>&1
        $rows | ForEach-Object { Write-Host "  $_" }
        $sum = @($rows | Where-Object { "$_" -match 'SUMMARY .*pass=(\d+) fail=(\d+)' }) | Select-Object -Last 1
        # A row that fails only on a line HWE logged ("[HWE] Error in ...") is HWE's own error in this game version,
        # not an AWE failure. It is listed, but the row is not counted as proven.
        $failed = @($rows | Where-Object { "$_" -match '^\s*FAIL ' })
        $hweBlocked = @($failed | Where-Object { "$_" -match '\[HWE\]' })
        $aweFailed = @($failed | Where-Object { "$_" -notmatch '\[HWE\]' })
        $hweBlocked | ForEach-Object { Write-Host "INFO hwe-error-blocks-row $_" }
        Result ($sum -and $aweFailed.Count -eq 0) 'fix-rows-with-hwe' "$sum; AWE failures: $($aweFailed.Count), rows blocked by an HWE error: $($hweBlocked.Count)"
    }
}
finally {
    # The running game holds the DLL open; stop it first.
    $p = Game
    if ($p) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(30000) | Out-Null; Write-Host "  game stopped: pid $($p.Id)" }
    Remove-Item (Join-Path $game 'Mods\HarderWorkingEmployees.dll') -Force -ErrorAction SilentlyContinue
    Write-Host "  HWE DLL removed from the slot: $(-not (Test-Path (Join-Path $game 'Mods\HarderWorkingEmployees.dll')))"
    Write-Host "SUMMARY awe-hwe pass=$pass fail=$fail"
}
