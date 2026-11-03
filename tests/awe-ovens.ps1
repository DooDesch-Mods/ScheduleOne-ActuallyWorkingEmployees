<#
A chemist walks through tightly placed lab ovens and a cauldron with both collision switches on, and is blocked
again with them off.

    pwsh -File tests/awe-ovens.ps1 -Slot mptest2 [-OutDir <folder>]

'awe ovenrig place' builds a closed ring of eight lab ovens (each on its table) on a free patch of an owned property,
a cauldron outside it, and hires a chemist when needed. The player is teleported next to the ring, inside the 25 m in
which NPCMovement turns obstacle avoidance back on. 'awe ovenrig walk' warps the chemist into the ring and sends him
to a point behind the cauldron.
  on:  general.disableemployeecollision and general.disablefurniturecollision true -> he arrives, the path is complete,
       no oven obstacle is enabled, NPCMovement's avoidance flag is off.
  off: both false -> the ring is closed again: he does not arrive, the path is not complete, every obstacle is enabled.
Clears the rig and resets both options at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$OutDir
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Wait-Seconds([double]$s) { $t = (Get-Date).AddSeconds($s); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 250 } }
function Set-Switches([string]$v) {
    S1 run "awe set general.disableemployeecollision $v" | Out-Null
    S1 run "awe set general.disablefurniturecollision $v" | Out-Null
    $c = S1 run 'awe collision'
    Write-Host "  collision ($v): $(($c -split "`n" | Where-Object { $_ -match '\[Collision\]' } | Select-Object -Last 1))"
}
function Finish {
    S1 run 'awe ovenrig clear' | Out-Null
    S1 run 'awe reset general.disableemployeecollision' | Out-Null
    S1 run 'awe reset general.disablefurniturecollision' | Out-Null
    S1 run 'awe collision' | Out-Null
    Write-Host "SUMMARY ovens pass=$pass fail=$fail"
    exit ([int]($fail -gt 0))
}
# Starts a walk (retrying while a hired chemist spawns) and waits for its result line.
function Walk([string]$label) {
    $started = $null
    for ($i = 0; $i -lt 10 -and -not $started; $i++) {
        $w = S1 run 'awe ovenrig walk'
        if ($w -match 'walk started: (.*)') { $started = $Matches[1] } else { Wait-Seconds 2 }
    }
    if (-not $started) { Result $false "$label-walk-started" "no walk: $($w.Split("`n")[-1])"; return $null }
    Write-Host "  $label walk: $started"
    if ($OutDir) { Wait-Seconds 3; Shot "ovens-$label.png" }
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        $s = S1 run 'awe ovenrig status'
        if ($s -match 'walking=False last=(reached=.*)') { return $Matches[1] }
        Wait-Seconds 1
    }
    Result $false "$label-walk-ended" 'no result after 40 s'
    return $null
}
function Shot([string]$name) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $p = (S1 shot).Split("`n")[-1].Trim()
    if (Test-Path $p) { $dst = Join-Path $OutDir $name; Copy-Item $p $dst -Force; Write-Host "  shot $dst" } else { Write-Host "  shot failed: $p" }
}

S1 run 'awe ovenrig clear' | Out-Null
$place = S1 run 'awe ovenrig place'
$line = ($place -split "`n" | Where-Object { $_ -match '\[ovenrig\] placed' } | Select-Object -Last 1)
Result ($line -match 'placed 9 item') 'rig-placed' "$line"
if (-not ($line -match 'player=([-\d.]+),([-\d.]+),([-\d.]+)')) { Write-Host ($place.Split("`n") | Select-Object -Last 3); Finish }
$px = [double]$Matches[1]; $py = [double]$Matches[2]; $pz = [double]$Matches[3]
$tp = S1 tp $px ($py + 0.2) $pz
Write-Host "  tp: $($tp.Split("`n")[-1])"
Wait-Seconds 2

# -- both switches on: the chemist walks through an oven and the cauldron --
Set-Switches 'true'
Wait-Seconds 2
$on = Walk 'on'
if ($on) {
    Write-Host "  on: $on"
    Result ($on -match 'reached=True') 'on-reached' $on
    Result ($on -match 'path=PathComplete') 'on-path-complete' ''
    Result ($on -match 'obstaclesOn=0/\d+') 'on-obstacles-off' ''
    Result ($on -match 'avoidanceFlag=False') 'on-avoidance-flag-off' ''
    $d = if ($on -match 'playerDist=([\d.]+)') { [double]$Matches[1] } else { 999 }
    Result ($d -lt 25) 'on-player-near' "playerDist=$d m"
}

# -- both switches off: vanilla carving closes the ring again --
Set-Switches 'false'
Wait-Seconds 3
$off = Walk 'off'
if ($off) {
    Write-Host "  off: $off"
    Result ($off -match 'reached=False') 'off-blocked' $off
    Result ($off -notmatch 'path=PathComplete') 'off-path-not-complete' ''
    Result ($off -match 'obstaclesOn=(\d+)/\1 ') 'off-obstacles-on' ''
    $d = if ($off -match 'playerDist=([\d.]+)') { [double]$Matches[1] } else { 999 }
    Result ($d -lt 25) 'off-player-near' "playerDist=$d m"
}

$doc = S1 run 'awe doctor'
Write-Host "  doctor: $(($doc -split "`n" | Select-Object -Last 2) -join ' | ')"
Finish
