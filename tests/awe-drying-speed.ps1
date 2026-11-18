<#
Drying rack speed (AWE#46): drying minutes gained per game minute must be the vanilla multiplier (temperature) times
DryingRackSpeed, at every speed and on every path the game advances a drying operation.

    pwsh -File tests/awe-drying-speed.ps1 -Slot mptest2 [-Speeds 0.25,1,10] [-LiveMinutes 12] [-Preloaded 10]

Needs the Debug build ('awe dryrack'). 'awe dryrack setup' uses or places one drying rack and starts a drying operation
the way a player does. Per speed:
  <speed>-skip   'awe dryrack pass 30' calls DryingRack.OnTimePass(30) once, the path a time skip (sleeping) takes.
  <speed>-live   'awe dryrack mark', then -LiveMinutes game minutes of normal play, the per-minute path.
  <speed>-ui     'awe dryrack ui' opens the rack's UI; the time left it shows matches the expected rate.
The option is changed live with 'awe set' before each speed, so every row after the first is a live change.
-Preloaded <speed>: the slot was started with that DryingRackSpeed and EnableStationSpeed=true already in
UserData/ActuallyWorkingEmployees.cfg (AWE's own file; MelonPreferences.cfg is only read on the first start); the row
'preloaded-live' measures it before any 'awe set'.
Each row passes within 2 % (plus 0.01) of the expected rate. Resets both options at the end. Prints PASS or FAIL per
check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$Speeds = '0.25,1,10',
    [int]$LiveMinutes = 12,
    [double]$Preloaded = 0
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
$env:PYTHONIOENCODING = 'utf-8'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python -I $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function LastLine([string]$text, [string]$pattern) { ($text -split "`n" | Where-Object { $_ -match $pattern } | Select-Object -Last 1) }
function Num([string]$line, [string]$key) {
    if ($line -match "(?:^|\s)$key=(-?\d+(?:\.\d+)?)") { return [double]::Parse($Matches[1], $inv) }
    return [double]::NaN
}
function Near([double]$got, [double]$want) { -not [double]::IsNaN($got) -and [math]::Abs($got - $want) -le (0.02 * $want + 0.01) }
function Dry([string]$cmd) { LastLine (S1 run "awe dryrack $cmd") '\[dryrack\]' }
function Finish {
    S1 run 'awe dryrack clear' | Out-Null
    S1 run 'awe reset stations.dryingrackspeed' | Out-Null
    S1 run 'awe reset stations.enablestationspeed' | Out-Null
    Write-Host "SUMMARY drying-speed pass=$pass fail=$fail"
    exit ([int]($fail -gt 0))
}
# Waits until the game clock has moved -LiveMinutes past the mark, then reads the live rate.
function Live([string]$tag) {
    $mark = Dry 'mark'
    if ($mark -notmatch 'mark at') { Result $false "$tag-live" "mark failed: $mark"; return }
    $deadline = (Get-Date).AddSeconds($LiveMinutes * 4 + 60)
    $st = ''
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1500
        $st = Dry 'status'
        if ((Num $st 'minutes') -ge $LiveMinutes) { break }
    }
    $rate = Num $st 'rate'; $want = Num $st 'expected'
    Result (((Num $st 'minutes') -ge $LiveMinutes) -and (Near $rate $want)) "$tag-live" "rate=$rate expected=$want minutes=$(Num $st 'minutes')"
    return $st
}

S1 run 'settimescale 1' | Out-Null
S1 run 'awe dryrack clear' | Out-Null
$setup = ''
for ($i = 0; $i -lt 5; $i++) {
    $setup = Dry 'setup'
    if ($setup -match 'setup:') { break }
    Start-Sleep -Seconds 2
}
Result ($setup -match 'setup:') 'rack-setup' "$setup"
if ($setup -notmatch 'setup:') { Finish }
# StartOperation goes through a server RPC; the operation shows up a moment later.
for ($i = 0; $i -lt 10; $i++) { if ((Dry 'status') -match 'status:') { break }; Start-Sleep -Milliseconds 500 }
if ($setup -match 'rack=(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)') {
    S1 tp $Matches[1] ([double]::Parse($Matches[2], $inv) + 1).ToString($inv) ([double]::Parse($Matches[3], $inv) + 2).ToString($inv) | Out-Null
}

if ($Preloaded -gt 0) {
    $st = Live 'preloaded'
    $sp = Num $st 'option'
    Result ([math]::Abs($sp - $Preloaded) -lt 1e-3 -and $st -match 'enabled=True') 'preloaded-option' "option=$sp enabled from file: $($st -match 'enabled=True')"
}

S1 run 'awe set stations.enablestationspeed true' | Out-Null
# -Speeds '' runs only the setup and the preloaded rows.
foreach ($speed in ($Speeds -split ',' | Where-Object { $_.Trim() } | ForEach-Object { [double]::Parse($_.Trim(), $inv) })) {
    $sp = $speed.ToString($inv)
    $tag = "x$sp"
    $set = S1 run "awe set stations.dryingrackspeed $sp"
    Write-Host "  set: $(LastLine $set 'dryingrackspeed')"
    $p = Dry 'pass 30'
    Result (Near (Num $p 'rate') (Num $p 'expected')) "$tag-skip" "rate=$(Num $p 'rate') expected=$(Num $p 'expected')"
    $ui = @((S1 run 'awe dryrack ui') -split "`n" | Where-Object { $_ -match '\[dryrack\] ui: entry=' })
    S1 run 'awe dryrack ui close' | Out-Null
    $bad = @($ui | Where-Object { $_ -notmatch 'match=True' })
    Result ($ui.Count -gt 0 -and $bad.Count -eq 0) "$tag-ui" "$($ui.Count) entr(y/ies), $($bad.Count) wrong: $(($ui | Select-Object -First 1) -replace '^.*ui: ', '')"
    Live $tag | Out-Null
}
Finish
