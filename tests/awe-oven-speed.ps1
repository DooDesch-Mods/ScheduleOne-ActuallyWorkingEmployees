<#
Lab oven speed (AWE#32): the oven timer, the real finish (ding, progress stops), the chemist's ready checks and the
chemist's collect must land on the same cook minute at every LabOvenSpeed.

    pwsh -File tests/awe-oven-speed.ps1 -Slot mptest2 [-Speeds 1,2.5,10] [-TimeScale 5]

'awe ovenrig place' builds eight lab ovens and hires a chemist. Per speed, 'awe ovencook setup' assigns a free oven to
the chemist and fills it with liquid meth, so the chemist starts the cook himself; the game runs at -TimeScale
(vanilla 'settimescale'). 'awe ovencook status' lists every event as name@progress/scaled-seconds:
  started, timer-zero (label shows 00:00), harvest-ready (LabOven.IsReadyForHarvest, how the chemist picks an oven),
  complete (OvenCookOperation.IsComplete, which plays the ding and stops the count), ready (OvenCookOperation.IsReady,
  what the chemist's collect action waits for), progress-stopped, collected (operation gone, output filled).
Expected cook minutes: round(module / speed), at least 1; speed 1 is the vanilla module time. Every event must sit on
that minute (one minute of polling slack), and the collect must follow the timer within -CollectSlack seconds of
scaled game time.
Resets the options, the time scale and the rig at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$Speeds = '1,2.5,10',
    [double]$TimeScale = 5,
    [int]$CollectSlack = 60,
    [int]$MaxSeconds = 600
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function Wait-Seconds([double]$s) { $t = (Get-Date).AddSeconds($s); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 250 } }
function LastLine([string]$text, [string]$pattern) { ($text -split "`n" | Where-Object { $_ -match $pattern } | Select-Object -Last 1) }
function Finish {
    S1 run 'settimescale 1' | Out-Null
    S1 run 'awe ovencook clear' | Out-Null
    S1 run 'awe ovenrig clear' | Out-Null
    S1 run 'awe reset stations.labovenspeed' | Out-Null
    S1 run 'awe reset stations.enablestationspeed' | Out-Null
    Write-Host "SUMMARY oven-speed pass=$pass fail=$fail"
    exit ([int]($fail -gt 0))
}
function Events([string]$status) {
    $map = @{}
    if ($status -match 'events=(\S*)') {
        foreach ($e in ($Matches[1] -split ',')) {
            if ($e -match '^([\w-]+)@(-?\d+)(?:/(-?\d+))?$') { $map[$Matches[1]] = @([int]$Matches[2], $(if ($Matches[3]) { [int]$Matches[3] } else { 0 })) }
        }
    }
    return $map
}

S1 run 'settimescale 1' | Out-Null
S1 run 'awe ovencook clear' | Out-Null
S1 run 'awe ovenrig clear' | Out-Null
$place = S1 run 'awe ovenrig place'
$line = LastLine $place '\[ovenrig\] placed'
Result ($line -match 'placed 9 item') 'rig-placed' "$line"
if ($line -match 'player=([-\d.]+),([-\d.]+),([-\d.]+)') {
    $tp = S1 tp $Matches[1] ([double]::Parse($Matches[2], $inv) + 0.2).ToString($inv) $Matches[3]
    Write-Host "  tp: $($tp.Split("`n")[-1])"
} else { Write-Host ($place.Split("`n") | Select-Object -Last 3); Finish }
S1 run 'awe set stations.enablestationspeed true' | Out-Null

foreach ($speed in ($Speeds -split ',' | ForEach-Object { [double]::Parse($_.Trim(), $inv) })) {
    $sp = $speed.ToString($inv)
    $tag = "x$sp"
    S1 time 0700 | Out-Null
    $set = S1 run "awe set stations.labovenspeed $sp"
    Write-Host "  set: $(LastLine $set 'labovenspeed')"
    # The hired chemist needs a few seconds to spawn before the first setup.
    $setup = $null
    for ($i = 0; $i -lt 10; $i++) {
        $setup = LastLine (S1 run 'awe ovencook setup') '\[ovencook\]'
        if ($setup -match 'setup:') { break }
        Wait-Seconds 2
    }
    Result ($setup -match 'setup:') "$tag-setup" "$setup"
    if (-not ($setup -match 'setup:')) { continue }
    S1 run "settimescale $($TimeScale.ToString($inv))" | Out-Null

    # A game minute takes a few real seconds even at a raised time scale, so the wait ends on the collect, or 120 s
    # after the count stopped without one, or after the hard cap.
    $deadline = (Get-Date).AddSeconds($MaxSeconds)
    $stoppedAt = $null
    $status = ''
    while ((Get-Date) -lt $deadline) {
        $status = LastLine (S1 run 'awe ovencook status') '\[ovencook\]'
        if ($status -match 'collected@') { break }
        if (-not $stoppedAt -and $status -match 'progress-stopped@') { $stoppedAt = Get-Date }
        if ($stoppedAt -and ((Get-Date) - $stoppedAt).TotalSeconds -gt 120) { Write-Host '  no collect 120 s after the count stopped'; break }
        Wait-Seconds 1
    }
    if ((Get-Date) -ge $deadline) { Write-Host "  hard cap of $MaxSeconds s reached" }
    S1 run 'settimescale 1' | Out-Null
    Write-Host "  status: $status"
    $ev = Events $status
    Result ($ev.ContainsKey('started')) "$tag-chemist-started" ''
    if (-not $ev.ContainsKey('started')) { continue }
    $module = $ev['module'][0]
    $want = if ([math]::Abs($speed - 1) -lt 1e-4) { $module } else { [math]::Max(1, [int][math]::Round($module / $speed, [MidpointRounding]::AwayFromZero)) }
    Write-Host "  module=$module want=$want duration=$($ev['duration'][0])"
    Result ([math]::Abs($ev['duration'][0] - $want) -le 1) "$tag-duration" "GetCookDuration=$($ev['duration'][0]) want=$want"
    foreach ($n in 'timer-zero', 'harvest-ready', 'complete', 'ready') {
        $has = $ev.ContainsKey($n)
        $at = if ($has) { $ev[$n][0] } else { -1 }
        Result ($has -and [math]::Abs($at - $want) -le 1) "$tag-$n" "at=$at want=$want"
    }
    $col = $ev.ContainsKey('collected'); $tz = $ev.ContainsKey('timer-zero')
    # The count stops at the finish: the last progress before the collect is the finish minute.
    $last = if ($col) { $ev['collected'][0] } else { -1 }
    Result ($col -and [math]::Abs($last - $want) -le 1) "$tag-count-stops" "last progress=$last want=$want"
    $delay = if ($col -and $tz) { $ev['collected'][1] - $ev['timer-zero'][1] } else { -1 }
    Result ($col -and $tz -and $delay -ge 0 -and $delay -le $CollectSlack) "$tag-collected" "scaled seconds after timer 00:00=$delay (max $CollectSlack)"
}
Finish
