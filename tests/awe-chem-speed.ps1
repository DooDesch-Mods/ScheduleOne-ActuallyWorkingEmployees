<#
Chemistry station speed (AWE#61): at ChemistryStationSpeed s a chemistry cook takes round(game time / s) game minutes,
the station finalizes on that minute, and the chemist carries the product to the destination rack afterwards.

    pwsh -File tests/awe-chem-speed.ps1 -Slot mptest2 [-Speeds 1,2] [-TimeScale 10]

'awe chemcook place' builds a chemistry station and a storage rack and hires a chemist. Per speed, 'awe chemcook
setup' selects the first recipe, sets the rack as destination and fills the ingredients, so the chemist starts the
cook himself. 'awe chemcook status' lists the events as name@game-minutes-since-start:
  started, complete (ChemistryCookOperation.IsComplete), finalized (operation gone, output filled), moved (output carried).
Checks per speed: the recipe's cook time is round(game / s); complete and finalized land on that minute (2 minutes of
polling slack); the chemist moved the output. Across speeds: the cook at the highest speed took about game / s.
Resets the options, the time scale and the rig at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$Speeds = '1,2',
    [double]$TimeScale = 10,
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
    S1 run 'awe chemcook clear' | Out-Null
    S1 run 'awe reset stations.chemistrystationspeed' | Out-Null
    S1 run 'awe reset stations.enablestationspeed' | Out-Null
    Write-Host "SUMMARY chem-speed pass=$pass fail=$fail"
    exit ([int]($fail -gt 0))
}
function Events([string]$status) {
    $map = @{}
    if ($status -match 'events=(\S*)') {
        foreach ($e in ($Matches[1] -split ',')) { if ($e -match '^([\w-]+)@(-?\d+)$') { $map[$Matches[1]] = [int]$Matches[2] } }
    }
    return $map
}

S1 run 'settimescale 1' | Out-Null
S1 run 'awe chemcook clear' | Out-Null
$place = S1 run 'awe chemcook place'
$line = LastLine $place '\[chemcook\] placed'
Result ($line -match 'placed 2 item') 'rig-placed' "$line"
if ($line -match 'player=([-\d.]+),([-\d.]+),([-\d.]+)') {
    $tp = S1 tp $Matches[1] ([double]::Parse($Matches[2], $inv) + 0.2).ToString($inv) $Matches[3]
    Write-Host "  tp: $($tp.Split("`n")[-1])"
} else { Write-Host ($place.Split("`n") | Select-Object -Last 3); Finish }
S1 run 'awe set stations.enablestationspeed true' | Out-Null

$took = @{}
$game = 0
foreach ($speed in ($Speeds -split ',' | ForEach-Object { [double]::Parse($_.Trim(), $inv) })) {
    $sp = $speed.ToString($inv)
    $tag = "x$sp"
    S1 time 0700 | Out-Null
    $set = S1 run "awe set stations.chemistrystationspeed $sp"
    Write-Host "  set: $(LastLine $set 'chemistrystationspeed')"
    Wait-Seconds 2
    $setup = $null
    for ($i = 0; $i -lt 15; $i++) {
        $setup = LastLine (S1 run 'awe chemcook setup') '\[chemcook\]'
        if ($setup -match 'setup:') { break }
        Wait-Seconds 2
    }
    Result ($setup -match 'setup:.*destination=route') "$tag-setup" "$setup"
    if (-not ($setup -match 'cook=(\d+) min game=(\d+)')) { continue }
    $cook = [int]$Matches[1]; $game = [int]$Matches[2]
    $want = if ([math]::Abs($speed - 1) -lt 1e-4) { $game } else { [math]::Max(1, [int][math]::Round($game / $speed)) }
    Result ($cook -eq $want) "$tag-recipe-time" "cook=$cook want=$want game=$game"
    S1 run "settimescale $($TimeScale.ToString($inv))" | Out-Null

    $deadline = (Get-Date).AddSeconds($MaxSeconds)
    $finalAt = $null
    $status = ''
    while ((Get-Date) -lt $deadline) {
        $status = LastLine (S1 run 'awe chemcook status') '\[chemcook\]'
        if ($status -match 'moved@') { break }
        if (-not $finalAt -and $status -match 'finalized@') { $finalAt = Get-Date }
        if ($finalAt -and ((Get-Date) - $finalAt).TotalSeconds -gt 90) { Write-Host '  no move 90 s after the finish'; break }
        Wait-Seconds 1
    }
    if ((Get-Date) -ge $deadline) { Write-Host "  hard cap of $MaxSeconds s reached" }
    S1 run 'settimescale 1' | Out-Null
    Write-Host "  status: $status"
    $ev = Events $status
    Result ($ev.ContainsKey('started')) "$tag-chemist-started" ''
    if (-not $ev.ContainsKey('started')) { continue }
    foreach ($n in 'complete', 'finalized') {
        $at = if ($ev.ContainsKey($n)) { $ev[$n] } else { -1 }
        Result ($at -ge 0 -and [math]::Abs($at - $want) -le 2) "$tag-$n" "at=$at want=$want"
    }
    Result ($ev.ContainsKey('moved')) "$tag-chemist-moved" "$(if ($ev.ContainsKey('moved')) { "at=$($ev['moved'])" } else { 'no move' })"
    if ($ev.ContainsKey('finalized')) { $took[$speed] = $ev['finalized'] }
}
$keys = @($took.Keys | Sort-Object)
if ($keys.Count -ge 2 -and $game -gt 0) {
    $hi = $keys[-1]
    $ratio = $took[$hi] / [double]$game
    Result ([math]::Abs($ratio - 1 / $hi) -le 0.05) 'speed-ratio' ("x$hi took $($took[$hi]) of $game game min, ratio {0:0.000} want {1:0.000}" -f $ratio, (1 / $hi))
}
Finish
