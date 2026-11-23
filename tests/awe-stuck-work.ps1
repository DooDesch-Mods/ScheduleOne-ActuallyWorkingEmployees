<#
Stuck-work watchdog (AWE#62): an employee whose work routine died stands at the station with its task still active.
Fix 7 must release that task after StuckWorkSeconds, log it at warning level, and the employee must pick the work up
again. A normal cook in place must not trigger it.

    pwsh -File tests/awe-stuck-work.ps1 -Slot mptest2 [-StuckSeconds 30]

Uses the chemistry rig of 'awe chemcook': the chemist walks to the station and starts the cook in place. While he
does, 'awe stuckwork kill' ends his work coroutine the way an exception does and leaves the task active.
Checks: the rig and setup work; the kill hit a chemist working in place; no restart happened before the kill; the
restart came after StuckWorkSeconds (not earlier, at most 25 s late); the log holds the [Fix7] warning; the chemist
then started the cook. Resets options and the rig at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [int]$StuckSeconds = 30
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$env:PYTHONIOENCODING = 'utf-8'
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Wait-Seconds([double]$s) { $t = (Get-Date).AddSeconds($s); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 250 } }
function LastLine([string]$text, [string]$pattern) { ($text -split "`n" | Where-Object { $_ -match $pattern } | Select-Object -Last 1) }
# Fix 7 also restarts other employees that hang on their own (a fresh save has some), so the checks count only the chemist's restarts.
function ChemRestarts([string]$name) { @((S1 log --grep "[Fix7] restarted $name (" -n 50) -split "`n" | Where-Object { $_ -match 'Fix7\] restarted' }).Count }
function Finish {
    S1 run 'awe chemcook clear' | Out-Null
    S1 run 'awe reset reliability.stuckworkseconds' | Out-Null
    Write-Host "SUMMARY stuck-work pass=$pass fail=$fail"
    exit ([int]($fail -gt 0))
}

S1 run 'settimescale 1' | Out-Null
S1 run 'awe chemcook clear' | Out-Null
$set = S1 run "awe set reliability.stuckworkseconds $StuckSeconds"
Write-Host "  set: $(LastLine $set 'stuckworkseconds')"
$place = S1 run 'awe chemcook place'
$line = LastLine $place '\[chemcook\] placed'
Result ($line -match 'placed [23] item') 'rig-placed' "$line"
if ($line -match 'player=([-\d.]+),([-\d.]+),([-\d.]+)') {
    S1 tp $Matches[1] ([double]::Parse($Matches[2], $inv) + 0.2).ToString($inv) $Matches[3] | Out-Null
} else { Finish }
S1 time 0700 | Out-Null

$setup = $null
for ($i = 0; $i -lt 15; $i++) {
    $setup = LastLine (S1 run 'awe chemcook setup') '\[chemcook\]'
    if ($setup -match 'setup:') { break }
    Wait-Seconds 2
}
Result ($setup -match 'setup:') 'setup' "$setup"
if (-not ($setup -match 'setup:')) { Finish }

# The chemist walks to the station, then works in place for a few seconds: that is the moment to kill. Vanilla starts the
# cook coroutine on the next minute pass after arrival; a kill in the first second finds no coroutine yet, and the chemist
# then cooks normally. So the kill waits until he has stood at the station for 3 s.
$chemist = if ($setup -match 'assigned to (.+?)( \(already\))?, destination') { $Matches[1] } else { '' }
Write-Host "  chemist: $chemist"
$before = ChemRestarts $chemist
$killed = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    $w = LastLine (S1 run 'awe stuckwork') ([regex]::Escape($chemist) + ' \(Chemist\): behaviour=StartChemistryStationBehaviour work=True moving=False stillFor=')
    if (-not ($w -match 'stillFor=(\d+)s' -and [int]$Matches[1] -ge 3)) { Wait-Seconds 0.5; continue }
    $k = LastLine (S1 run "awe stuckwork kill $chemist") 'killed the work routine|works in place'
    if ($k -match 'killed the work routine') { $killed = $k; break }
    Wait-Seconds 0.5
}
$killedAt = Get-Date
Result ($null -ne $killed) 'killed-in-place' "$killed"
if (-not $killed) { Finish }
$atKill = ChemRestarts $chemist
Result ($before -ge 0 -and $atKill -eq $before) 'no-restart-before-kill' "before=$before atKill=$atKill"

$after = $atKill
$deadline = (Get-Date).AddSeconds($StuckSeconds + 60)
while ((Get-Date) -lt $deadline) {
    $after = ChemRestarts $chemist
    if ($after -gt $atKill) { break }
    Wait-Seconds 1
}
$took = ((Get-Date) - $killedAt).TotalSeconds
# The watch counts from the last progress, so an employee that already stood still before the kill restarts earlier.
$head = if ($killed -match 'still for (\d+) s') { [int]$Matches[1] } else { 0 }
$due = [Math]::Max(0, $StuckSeconds - $head)
Result ($after -gt $atKill) 'restarted' "restarts $atKill -> $after"
Result ($after -gt $atKill -and $took -ge ($due - 2) -and $took -le ($due + 25)) 'restart-timing' ("{0:0} s after the kill, due after {1} s (limit $StuckSeconds s, still {2} s at the kill)" -f $took, $due, $head)

$log = S1 log --grep '[Fix7] restarted' -n 20   # plain substring, not a regex
$warn = LastLine $log 'Fix7\] restarted'
Result ($warn -match 'restarted' -and $warn -match 'W\]|WARN|Warning|\[Warn') 'warn-logged' "$warn"

$deadline = (Get-Date).AddSeconds(90)
$status = ''
while ((Get-Date) -lt $deadline) {
    $status = LastLine (S1 run 'awe chemcook status') '\[chemcook\]'
    if ($status -match 'started@') { break }
    Wait-Seconds 1
}
Result ($status -match 'started@') 'resumed-cook' "$status"
Finish
