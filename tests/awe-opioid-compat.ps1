<#
Opioid Expansion compatibility (AWE#51): the native build folds DryingRack.GetDryMultiplier and
Pot.GetTemperatureGrowthMultiplier into one function. A second mod that patches the pot method after AWE must not crash the
game, and the drying rack UI must still show the time left at AWE's drying speed.

    pwsh -File tests/awe-opioid-compat.ps1 -Slot mptest2 [-ExpectCrash] [-Seconds 20] [-Speed 10]

Needs the Debug build ('awe native', 'awe dryrack'). 'awe native opioid' patches Pot.GetTemperatureGrowthMultiplier under a
foreign Harmony ID, the way Opioid Expansion does, and calls both methods once.
  shared-address   both methods report the same native address (the precondition of the crash).
  awe-off-dry      AWE has no patch on DryingRack.GetDryMultiplier.
  probe-call       the probe patch is applied and both calls return.
  survives         the game still answers -Seconds later, and the foreign postfix ran on pot updates in that time.
  ui-time-left     at DryingRackSpeed -Speed, the rack UI shows the time left at the scaled rate.
-ExpectCrash: the build before the fix. Checks 'shared-address' and 'crash' (the game is gone within -Seconds) and skips
the rest. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [switch]$ExpectCrash,
    [int]$Seconds = 20,
    [double]$Speed = 10
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
function Native([string]$cmd) { LastLine (S1 run "awe native $cmd") '\[native\]' }
function Hits() { $l = Native 'status'; if ($l -match 'hits=(\d+)') { return [int]$Matches[1] }; return -1 }
function Finish {
    Write-Host "SUMMARY opioid-compat pass=$pass fail=$fail"
    exit ([int]($fail -gt 0))
}

$addr = Native 'addr'
Result ($addr -match 'shared=True') 'shared-address' "$addr"

if ($ExpectCrash) {
    $probe = S1 run 'awe native opioid'
    Write-Host "  probe: $(LastLine $probe '\[native\]')"
    $deadline = (Get-Date).AddSeconds($Seconds)
    $alive = $true
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Hits) -lt 0) { $alive = $false; break }
    }
    Result (-not $alive) 'crash' "game gone within ${Seconds}s: $(-not $alive)"
    Finish
}

Result ($addr -match 'GetDryMultiplier=0x[0-9A-F]+ \(owners none\)') 'awe-off-dry' "$addr"

$probe = Native 'opioid'
Result ($probe -match 'calls returned') 'probe-call' "$probe"
$h0 = Hits
Start-Sleep -Seconds $Seconds
$h1 = Hits
Result ($h1 -gt $h0 -and $h0 -ge 0) 'survives' "alive after ${Seconds}s, foreign postfix hits $h0 -> $h1"
if ($h1 -lt 0) { Finish }

S1 run 'awe set stations.enablestationspeed true' | Out-Null
S1 run "awe set stations.dryingrackspeed $($Speed.ToString($inv))" | Out-Null
S1 run 'awe dryrack clear' | Out-Null
$setup = ''
for ($i = 0; $i -lt 5; $i++) {
    $setup = LastLine (S1 run 'awe dryrack setup') '\[dryrack\]'
    if ($setup -match 'setup:') { break }
    Start-Sleep -Seconds 2
}
if ($setup -notmatch 'setup:') { Result $false 'ui-time-left' "rack setup failed: $setup" }
else {
    # StartOperation goes through a server RPC; the operation shows up a moment later.
    for ($i = 0; $i -lt 10; $i++) { if ((LastLine (S1 run 'awe dryrack status') '\[dryrack\]') -match 'status:') { break }; Start-Sleep -Milliseconds 500 }
    $ui = S1 run 'awe dryrack ui'
    $rows = @($ui -split "`n" | Where-Object { $_ -match '\[dryrack\] ui: entry=' })
    $bad = @($rows | Where-Object { $_ -notmatch 'match=True' })
    Result ($rows.Count -gt 0 -and $bad.Count -eq 0) 'ui-time-left' "$($rows.Count) entr(y/ies), $($bad.Count) wrong: $(($rows | Select-Object -First 1))"
    S1 run 'awe dryrack ui close' | Out-Null
}
S1 run 'awe dryrack clear' | Out-Null
S1 run 'awe reset stations.dryingrackspeed' | Out-Null
S1 run 'awe reset stations.enablestationspeed' | Out-Null
S1 run 'awe native unprobe' | Out-Null
Finish
