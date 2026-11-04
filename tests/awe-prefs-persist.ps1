<#
Settings survive a restart while MelonPreferences.cfg cannot be saved.

    pwsh -File tests/awe-prefs-persist.ps1 -Slot mptest3 [-Build <worktree or catalog name>] [-Profile stack|main]

Another mod can break the save of the shared UserData/MelonPreferences.cfg (NoMoreTrash with Strain-o-nator writes a
key Tomlet cannot serialize). MelonLoader then skips every later save of that file for the session. This test puts
MelonLoader into the same state by making the shared file read-only while the game runs, changes an AWE option, quits
the game the way a player does (window close, so MelonLoader's quit save runs) and reads the option after a restart.

Rows:
  shared-value-read    the value written into the shared file before the start is what AWE reports (migration source)
  own-file-created     UserData/ActuallyWorkingEmployees.cfg exists after the start and holds that value
  set                  awe set took the new value
  own-file-written     the new value reached the AWE file before the quit
  graceful-quit        the game exited after the window close
  shared-save-failed   the log of that run shows MelonLoader's save error for MelonPreferences.cfg (the reproduced state)
  value-after-restart  after a restart AWE reports the new value
Prints PASS or FAIL per row and a SUMMARY line. -Build defaults to this worktree; pass ActuallyWorkingEmployees for
the main checkout to see the old behaviour.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$Build = '',
    [ValidateSet('stack', 'main')][string]$Profile = 'stack',
    [int]$Before = 11,
    [int]$After = 13
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'awe-lib.ps1')
$root = $script:Root
if (-not $Build) { $Build = Split-Path $script:RepoDir -Leaf }
$session = Join-Path $root 'Workspace\tools\session\session.ps1'
$game = Join-Path $root "_MPTest$($Slot -replace 'mptest','')\Schedule I"
$shared = Join-Path $game 'UserData\MelonPreferences.cfg'
$own = Join-Path $game 'UserData\ActuallyWorkingEmployees.cfg'
$log = Join-Path $game 'MelonLoader\Latest.log'
$key = 'botanist.maxpots'
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function Game { Get-Process 'Schedule I' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$game\*" } | Select-Object -First 1 }
function StopHard {
    $p = Game
    if ($p) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(30000) | Out-Null; Write-Host "  game stopped: pid $($p.Id)" }
}
function SetReadOnly([bool]$on) {
    if (Test-Path $shared) { (Get-Item $shared).IsReadOnly = $on }
}
# Sets MaxPots in [AWE_Botanist] of the shared file, adding the section or the line when missing.
function SetSharedMaxPots([int]$v) {
    $text = if (Test-Path $shared) { Get-Content -LiteralPath $shared -Raw } else { '' }
    $rx = '(?ms)^\[AWE_Botanist\]\r?\n(?<body>.*?)(?=^\[|\z)'
    $m = [regex]::Match($text, $rx)
    if (-not $m.Success) { $text = $text.TrimEnd() + "`r`n`r`n[AWE_Botanist]`r`nMaxPots = $v`r`n" }
    else {
        $body = $m.Groups['body'].Value
        $newBody = if ($body -match '(?m)^MaxPots\s*=') { $body -replace '(?m)^MaxPots\s*=.*$', "MaxPots = $v" } else { "MaxPots = $v`r`n" + $body }
        $text = $text.Substring(0, $m.Groups['body'].Index) + $newBody + $text.Substring($m.Groups['body'].Index + $m.Groups['body'].Length)
    }
    Set-Content -LiteralPath $shared -Value $text -NoNewline -Encoding utf8
}
function AweValue {
    $r = Invoke-Awe -Slot $Slot -Cmd "awe get $key" -Until ([regex]::Escape($key) + ' = ')
    $line = @($r.Texts | Where-Object { $_ -match ([regex]::Escape($key) + ' = (\S+)') }) | Select-Object -First 1
    if ($line -and $line -match ([regex]::Escape($key) + ' = (\S+)')) { return $Matches[1] }
    return "(no answer: $(($r.Raw | Select-Object -Last 2) -join ' | '))"
}

try {
    # ---- start 1: the value comes from the shared file -------------------------------------------------------
    StopHard
    SetReadOnly $false
    $buildOut = & pwsh -NoProfile -File $session -Slot $Slot -Build $Build -Profile $Profile -Save 0 -NoApp 2>&1 | ForEach-Object { "$_" }
    $buildExit = $LASTEXITCODE
    $buildOut | ForEach-Object { Write-Host $_ }
    $bad = @($buildOut | Where-Object { $_ -match 'is neither a catalog mod nor a folder' })
    if ($buildExit -ne 0 -or $bad.Count -gt 0) {
        Result $false 'session-build' "session.ps1 exit $buildExit; $($bad -join ' | ') (pass a catalog name or the worktree folder name)"
        return
    }
    StopHard
    SetSharedMaxPots $Before
    Remove-Item -LiteralPath $own -Force -ErrorAction SilentlyContinue
    & pwsh -NoProfile -File $session -Slot $Slot -KeepSlot -NoApp
    if ($LASTEXITCODE -ne 0) { Result $false 'session-start-1' "session.ps1 exit $LASTEXITCODE"; return }

    $v = AweValue
    Result ($v -eq "$Before") 'shared-value-read' "$key = $v, expected $Before"
    $ownText = if (Test-Path $own) { Get-Content -LiteralPath $own -Raw } else { '' }
    Result ($ownText -match "(?m)^MaxPots = $Before\s*$") 'own-file-created' $(if ($ownText) { "MaxPots line: $([regex]::Match($ownText, '(?m)^MaxPots.*$').Value)" } else { "$own missing" })

    # ---- change with the shared file unsaveable, then quit like a player -------------------------------------
    SetReadOnly $true
    $r = Invoke-Awe -Slot $Slot -Cmd "awe set $key $After" -Until ([regex]::Escape($key) + ' = ')
    $setLine = @($r.Texts | Where-Object { $_ -match ([regex]::Escape($key) + " = $After") }).Count
    Result ($setLine -ge 1) 'set' "lines with '$key = $After': $setLine"
    $waitErr = Wait-Until -TimeoutSec 10 -What 'MaxPots in the AWE file' -Condition {
        (Test-Path $own) -and ((Get-Content -LiteralPath $own -Raw) -match "(?m)^MaxPots = $After\s*$") }
    Result ($null -eq $waitErr) 'own-file-written' $(if ($waitErr) { $waitErr } else { "MaxPots = $After in $own" })

    $p = Game
    $closed = $false
    if ($p) {
        $null = $p.CloseMainWindow()
        $closed = $p.WaitForExit(90000)
    }
    Result $closed 'graceful-quit' $(if ($p) { "pid $($p.Id) exited after window close: $closed" } else { 'no game process' })
    if (-not $closed) { StopHard }
    $quitLog = Join-Path $env:TEMP "awe-prefs-quit-$Slot.log"
    Copy-Item -LiteralPath $log -Destination $quitLog -Force
    $err = @(Select-String -LiteralPath $quitLog -Pattern 'Error while Saving Preferences to .*MelonPreferences\.cfg')
    Result ($err.Count -ge 1) 'shared-save-failed' "lines: $($err.Count) (log copy $quitLog)"
    Select-String -LiteralPath $quitLog -Pattern 'Preferences Saved|settings saved to|was not written|Defaulting .*Fallback' |
        ForEach-Object { Write-Host "  quit log $($_.LineNumber): $($_.Line.Trim())" }

    # ---- start 2: the changed value must be back --------------------------------------------------------------
    SetReadOnly $false
    & pwsh -NoProfile -File $session -Slot $Slot -KeepSlot -NoApp
    if ($LASTEXITCODE -ne 0) { Result $false 'session-start-2' "session.ps1 exit $LASTEXITCODE"; return }
    $v = AweValue
    Result ($v -eq "$After") 'value-after-restart' "$key = $v, expected $After"
}
finally {
    SetReadOnly $false
    StopHard
    Write-Host "SUMMARY awe-prefs-persist build=$Build pass=$pass fail=$fail"
}
