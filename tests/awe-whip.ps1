<#
The whip (AWE#63) on the host.

    pwsh -File tests/awe-whip.ps1 -Slot mptest2 [-Shot]

Whips an employee of the save that is working (template 'played' has botanists). The idle case uses the rig of 'awe chemcook'.
Checks:
- registered: the item is in the registry and listed at the Gas-Mart(s);
- give: a whip lands in the inventory, hold puts it in hand and the held model is installed;
- boost: whipping a working employee multiplies his work speed by 1 + WhipBoostPercent/100;
- stacks: each further boost adds one stack (work speed 1 + n * percent/100) up to WhipMaxStacks, the next one is "max";
- refresh: every boost moves the end of all stacks to now + WhipBoostMinutes;
- walk: the walk speed rises with the stacks and stays at 6 m/s or below;
- expiry: after WhipBoostMinutes game minutes all stacks end together and both speeds are back;
- no-cooldown: a boost right after the expiry works;
- reset: WhipResetHits hits in one frame drop all stacks; the same hits spread over more than WhipResetSeconds do not;
- stuck: an employee whose work routine died (awe stuckwork kill) and who stands still for 10 s gets his task restarted;
- idle: a chemist without a station gets the idle answer with a restart, twice in a row (no cooldown);
- log: no exception from the whip in the log.
-Shot saves a screenshot with the whip in hand. Resets the rig at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [switch]$Shot
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
function LastLine([string]$text, [string]$pattern) { ($text -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ -match $pattern } | Select-Object -Last 1) }
function Num([string]$line, [string]$key) {
    if ($line -match ($key + '=([-\d.]+)')) { [double]::Parse($Matches[1], $inv) } else { -1 }
}
function Whip([string]$name) { LastLine (S1 run "awe whip use $name") '\[Whip\] use on|use: no employee' }
function Hits([int]$n, [string]$name) { LastLine (S1 run "awe whip hits $n $name") '\[Whip\] hits on|hits: ' }
function Boost([string]$name) { LastLine (S1 run "awe whip boost $name") '\[Whip\] boost on .*: \w+ \(' }
# The dev status: one line per employee with working, still, stacks, ends, workSpeed, walkDefault, walk; plus now= on the first line.
function State([string]$name) {
    $out = S1 run 'awe whip'
    $e = LastLine $out ("employee " + [regex]::Escape($name) + " working=")
    $h = LastLine $out '\[Whip\] registered='
    [pscustomobject]@{
        Line = $e; Head = $h
        Working = ($e -match 'working=1')
        Stacks = [int](Num $e 'stacks'); Ends = [int](Num $e 'ends'); Now = [int](Num $h 'now')
        Work = (Num $e 'workSpeed'); Walk = (Num $e 'walk'); WalkDefault = (Num $e 'walkDefault')
    }
}
function Opt([string]$id) {
    $l = LastLine (S1 run "awe get general.$id") ("(?i)$id")
    if ($l -match '=\s*([\d.]+)') { [double]::Parse($Matches[1], $inv) } else { -1 }
}
# Moves the game clock forward from the current game time (the clock runs while the script waits).
function Skip([int]$minutes) {
    $now = (State '').Now
    $day = ($now % 1440) + $minutes
    $t = '{0:00}{1:00}' -f ([math]::Floor($day / 60) % 24), ($day % 60)
    S1 time $t | Out-Null
    Write-Host "  clock -> $t"
}
function Finish {
    S1 run 'awe chemcook clear' | Out-Null
    Write-Host "SUMMARY whip pass=$pass fail=$fail"
    exit ([int]($fail -gt 0))
}

S1 run 'settimescale 1' | Out-Null
S1 run 'awe chemcook clear' | Out-Null
$pct = Opt 'whipboostpercent'; if ($pct -lt 0) { $pct = 50 }
$mins = [int](Opt 'whipboostminutes'); if ($mins -lt 0) { $mins = 60 }
$max = [int](Opt 'whipmaxstacks'); if ($max -lt 1) { $max = 3 }
$resetHits = [int](Opt 'whipresethits'); if ($resetHits -lt 2) { $resetHits = 5 }
$resetSecs = Opt 'whipresetseconds'; if ($resetSecs -le 0) { $resetSecs = 3 }
Write-Host "  options: +$pct% per stack for $mins min, max $max stacks, reset at $resetHits hits within $resetSecs s"

# --- registered and listed at the Gas-Mart
$st = LastLine (S1 run 'awe whip') '\[Whip\] registered='
Result ($st -match 'registered=1') 'registered' "$st"
$log = S1 log --grep '[Whip] listed' -n 50
$listed = LastLine $log 'listed in'
$at = @($log -split "`n" | Where-Object { $_ -match "listed at '" } | ForEach-Object { if ($_ -match "listed at '([^']+)'") { $Matches[1] } }) | Select-Object -Unique
Result ($listed -match 'listed in [1-9]' -and $at.Count -gt 0 -and @($at | Where-Object { $_ -notmatch '(?i)gas' }).Count -eq 0) 'listed-gasmart' ("$listed shops: " + ($at -join ', '))

# --- give and hold
$give = LastLine (S1 run 'awe whip give') '\[Whip\] (a whip was put|give:)'
Result ($give -match 'put into the inventory') 'give' "$give"
$hold = LastLine (S1 run 'awe whip hold') '\[Whip\] hold'
Wait-Seconds 2
$st = LastLine (S1 run 'awe whip') '\[Whip\] registered='
Result ($hold -match 'in hand' -and $st -match 'held=1' -and $st -match 'equipped=True') 'hold' "$hold | $st"
if ($Shot) {
    S1 run 'awe whip vm' | Out-Null
    Wait-Seconds 1.5
    $shotOut = S1 shot
    Write-Host "  shot: $(LastLine $shotOut '\.(png|jpg)')"
}

# --- swing: the click path without the mouse; arm animation, air sound, lash throw, then the crack at the snap
$sounds = LastLine (S1 log --grep '[Whip] sounds ready' -n 3) 'sounds ready'
$lash = LastLine (S1 log --grep '[Whip] lash in hand' -n 3) 'lash in hand'
$swingOut = S1 run 'awe whip swing'
$swing = LastLine $swingOut '\[Whip\] swing \d+:'
Wait-Seconds 1
$snap = LastLine (S1 log --grep '[Whip] snap:' -n 3) 'snap:'
Result ($sounds -match 'swing yes, crack yes' -and $lash -match 'found' -and $swing -match '(air|recorded crack) played, lash thrown' -and $snap -match 'crack (played|inside the recorded swing|recorded, layered played)') 'swing-sounds' "$sounds | $lash | $swing | $snap"

# --- lash at rest: standing still the tip stays within 1 cm; after a swing it is still again within 1.1 s (0.6 s swing + 0.5 s)
function Measure-Lash([double]$secs) {
    S1 run "awe whip lash measure $secs" | Out-Null
    Wait-Seconds ($secs + 0.8)
    LastLine (S1 log --grep '[Whip] lash measure:' -n 3) 'lash measure:'
}
Wait-Seconds 2
$still = Measure-Lash 3
$dev = if ($still -match 'max deviation ([\d.]+) cm') { [double]::Parse($Matches[1], $inv) } else { 99 }
Result ($dev -le 1.0) 'lash-still' "$still"
S1 run 'awe whip swing' | Out-Null
$settle = Measure-Lash 3
# The machete arm comes back from the swing on its own time; the lash itself (tip against the hanging pose of the hand
# of that frame) is still at most 0.5 s after the hand.
$hand = if ($settle -match 'hand still from ([\d.]+) s') { [double]::Parse($Matches[1], $inv) } else { 99 }
$own = if ($settle -match 'hanging pose [\d.]+ cm at most, settled from ([\d.]+) s') { [double]::Parse($Matches[1], $inv) } else { 99 }
Result ($own -le $hand + 0.5 -and $own -le 2.5) 'lash-settle' "$settle"
# --- turning: 90 degrees over 1 s like the mouse; the lash trails a little and is still again 0.5 s after the turn
Wait-Seconds 1.5
S1 run 'awe whip lash measure 3' | Out-Null
S1 run 'awe whip turn 90 1' | Out-Null
Wait-Seconds 3.8
$turn = LastLine (S1 log --grep '[Whip] lash measure:' -n 3) 'lash measure:'
$tdev = if ($turn -match 'max deviation ([\d.]+) cm') { [double]::Parse($Matches[1], $inv) } else { 99 }
# Judged against the hand: after a turn the vanilla viewmodel swings the hand back for about a second, the lash goes with it.
# The turn ends about 1.6 s into the measurement; the lash is still against the hand 0.5 s later.
$tfrom = if ($turn -match 'against the hand [\d.]+ cm at most, still from ([\d.]+) s') { [double]::Parse($Matches[1], $inv) } else { 99 }
Result ($tfrom -le 2.2 -and $tdev -le 25) 'lash-turn' "$turn"

# --- target: an employee of the save that runs a work behaviour (botanists water and harvest all day)
S1 time 0700 | Out-Null
function Workers {
    $out = S1 run 'awe whip'
    @($out -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ -match 'employee (.+?) working=1 ' } | ForEach-Object {
        if ($_ -match 'employee (.+?) working=1 ') { $Matches[1] } })
}
$r = ''; $s0 = $null; $target = $null
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $w = @(Workers)
    if ($w.Count -gt 0) {
        $target = $w[0]
        $s0 = State $target
        $r = Whip $target
        if ($r -match ': boost$') { break }
        Write-Host "  $target answered: $r"
        Wait-Seconds ($resetSecs + 0.5)   # keep the tries out of the reset window
    } else { Wait-Seconds 1 }
}
Result ($null -ne $target) 'employee-working' "$($s0.Line)"
if (-not $target) { Finish }
Write-Host "  target: $target"
$base = $s0.Work; $walk0 = $s0.Walk
$s = State $target
$want = $base * (1 + $s.Stacks * $pct / 100.0)
Result ($r -match ': boost$') 'boost' "$r"
# The boost notice is longer than the game's 200 px box; the whip widens the box instead of cutting the text.
$wide = LastLine (S1 log --grep 'notification widened' -n 3) 'notification widened by \d+'
Result ($null -ne $wide) 'notice-fits' "$wide"
Result ($base -gt 0 -and $s.Stacks -eq 1 -and [math]::Abs($s.Work - $want) -lt 0.01) 'boost-speed' ("work speed {0:0.###} -> {1:0.###}, expected {2:0.###}, stacks {3}" -f $base, $s.Work, $want, $s.Stacks)

# --- stacks up to the cap, every boost refreshes the end of all stacks (dev boost: no work check, no hit count)
Skip 10
Wait-Seconds 1.5
$okStacks = $true; $okRefresh = $true; $detail = @()
for ($n = $s.Stacks + 1; $n -le $max; $n++) {
    $r = Boost $target
    $s = State $target
    $want = $base * (1 + $n * $pct / 100.0)
    if (-not ($r -match ': boost \(' -and $s.Stacks -eq $n -and [math]::Abs($s.Work - $want) -lt 0.01)) { $okStacks = $false }
    if ([math]::Abs(($s.Ends - $s.Now) - $mins) -gt 2) { $okRefresh = $false }
    $detail += ("{0}: {1} stacks, work {2:0.###} (want {3:0.###}), ends-now {4}" -f $n, $s.Stacks, $s.Work, $want, ($s.Ends - $s.Now))
}
Result ($okStacks -and $detail.Count -gt 0) 'stacks' ($detail -join ' | ')
Result ($okRefresh -and $detail.Count -gt 0) 'refresh' ($detail -join ' | ')
$r = Boost $target
$sMax = State $target
$want = $base * (1 + $max * $pct / 100.0)
Result ($r -match ': max \(' -and $sMax.Stacks -eq $max -and [math]::Abs($sMax.Work - $want) -lt 0.01) 'max' ("$r | stacks $($sMax.Stacks), work {0:0.###}" -f $sMax.Work)
Result ($sMax.Walk -gt $walk0 -and $sMax.Walk -le 6.001) 'walk' ("walk {0:0.###} -> {1:0.###} m/s (default {2:0.###}, cap 6)" -f $walk0, $sMax.Walk, $sMax.WalkDefault)

# --- expiry: all stacks end together
Skip ($mins + 5)
$ended = $false; $after = $null
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    $after = State $target
    if ($after.Stacks -eq 0 -and [math]::Abs($after.Work - $base) -lt 0.01) { $ended = $true; break }
    Wait-Seconds 1
}
$endLine = LastLine (S1 log --grep '[Whip] boost on' -n 50) ' ended at game minute'
Result ($ended -and $endLine -match "\($max stack") 'expired' ("work {0:0.###}, walk {1:0.###} | $endLine" -f $after.Work, $after.Walk)

$r = Boost $target
Result ($r -match ': boost \(') 'no-cooldown' "$r"

# --- reset: the hits in one frame drop all stacks; spread over more than the window they do not
Boost $target | Out-Null
$r = Hits $resetHits $target
$s = State $target
$resetLine = LastLine (S1 log --grep ' s: boost dropped, task restarted.' -n 5) 'times within'
Result ($r -match ',reset$' -and $s.Stacks -eq 0 -and [math]::Abs($s.Work - $base) -lt 0.01 -and $resetLine) 'reset' ("$r | stacks $($s.Stacks), work {0:0.###} | $resetLine" -f $s.Work)
Wait-Seconds ($resetSecs + 0.5)
$r1 = Hits ($resetHits - 1) $target
Wait-Seconds ($resetSecs + 0.5)
$r2 = Hits 1 $target
Result ($r1 -match 'hits on' -and $r2 -match 'hits on' -and $r1 -notmatch 'reset' -and $r2 -notmatch 'reset') 'no-reset-spread' "$r1 | $r2"
Wait-Seconds ($resetSecs + 0.5)

# --- burst: 5 real clicks, one every 0.3 s, through the click gate onto the target in front of the camera
# The crack probe is a 0.25 m sphere: a botanist between tent poles can be out of reach from every side. Then the burst
# goes to the first working employee the probe reaches.
$bt = $target
$face = LastLine (S1 run "awe whip face $target") '\[Whip\] face'
if ($face -notmatch 'free view at') {
    $face = LastLine (S1 run 'awe whip face') '\[Whip\] face'
    if ($face -match 'face (.+?): spot=') { $bt = $Matches[1] }
}
if ($face -match 'free view at' -and $face -match 'spot=([-\d.]+),([-\d.]+),([-\d.]+) yaw=([-\d.]+)') {
    S1 tp $Matches[1] $Matches[2] $Matches[3] --yaw $Matches[4] | Out-Null
    Wait-Seconds 1
    S1 run "awe whip burst $resetHits 0.3" | Out-Null
    Wait-Seconds ($resetHits * 0.3 + 1.5)
    # Only the lines from the burst on: the 'reset' check above left a reset line of the same employee.
    $all = @((S1 log --grep '[Whip]' -n 80) -split "`n")
    $start = -1
    for ($i = 0; $i -lt $all.Count; $i++) { if ($all[$i] -match '\[Whip\] burst: \d+ clicks') { $start = $i } }
    $log = if ($start -ge 0) { ($all[$start..($all.Count - 1)]) -join "`n" } else { '' }
    $ignored = @($log -split "`n" | Where-Object { $_ -match 'click ignored: the last swing' }).Count
    $hitLines = @($log -split "`n" | Where-Object { $_ -match '\[Whip\] snap: crack (played|inside the recorded swing|recorded, layered played), click: ' -and $_ -match [regex]::Escape($bt) }).Count
    $burstReset = LastLine $log ([regex]::Escape($bt) + ' was hit .* times within .* s: boost dropped, task restarted')
    Result ($ignored -eq 0 -and $hitLines -ge $resetHits -and $burstReset) 'burst-reset' "ignored $ignored, hits on $bt $hitLines | $burstReset | $face"
} else { Result $false 'burst-reset' "rig: $face" }
Skip ($mins + 5)
Wait-Seconds ($resetSecs + 0.5)

# --- stuck: kill the work routine of an employee working in place, 10 s of standstill, whip that employee
$killed = $null; $stuck = $null
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    $k = LastLine (S1 run 'awe stuckwork kill') 'killed the work routine|no employee works in place'
    if ($k -match 'killed the work routine of (.+?) \(') { $killed = $k; $stuck = $Matches[1]; break }
    Wait-Seconds 0.5
}
Result ($null -ne $killed) 'stuck-kill' "$killed"
if ($killed) {
    Wait-Seconds 12
    $r = Whip $stuck
    Result ($r -match ': restart$') 'stuck-restart' "$r"
    $rl = LastLine (S1 log --grep '[Fix7] restarted' -n 20) 'whip from'
    Result ($rl -match ('restarted ' + [regex]::Escape($stuck))) 'stuck-restart-log' "$rl"
}

# --- idle: a chemist without a station has nothing to do; a second whip right after works the same (no cooldown)
$place = S1 run 'awe chemcook place'
if ((LastLine $place '\[chemcook\] placed') -match 'player=([-\d.]+),([-\d.]+),([-\d.]+)') {
    S1 tp $Matches[1] ([double]::Parse($Matches[2], $inv) + 0.2).ToString($inv) $Matches[3] | Out-Null
}
$setup = $null
for ($i = 0; $i -lt 15; $i++) {
    $setup = LastLine (S1 run 'awe chemcook setup') '\[chemcook\]'
    if ($setup -match 'setup:') { break }
    Wait-Seconds 2
}
if ($setup -match 'assigned to (.+?)( \(already\))?, destination') {
    $chemist = $Matches[1]
    S1 run 'awe chemcook clear' | Out-Null
    # A chemist that cooked before (another test in the same game) can still finish a carry task after the station is gone.
    $deadline = (Get-Date).AddSeconds(30)
    $st = State $chemist
    while ($st.Working -and (Get-Date) -lt $deadline) { Wait-Seconds 1; $st = State $chemist }
    Write-Host "  idle wait: $($st.Line)"
    $r = Whip $chemist
    $r2 = Whip $chemist
    Result ($r -match ': (idle|restart)$' -and $r2 -match ': (idle|restart)$') 'idle' "$r | $r2"
} else { Result $false 'idle' "rig: $(LastLine $place '\[chemcook\]') | $setup" }

# --- no exception from the whip (s1.py log --grep is a plain substring: read every whip line and filter here)
$err = (S1 log --grep 'Whip' -n 5000) + "`n" + (S1 log --grep 'whip' -n 5000)
$errLines = @($err -split "`n" | Where-Object { $_ -match '(?i)exception|error|failed' })
Result ($errLines.Count -eq 0) 'no-errors' ("{0} line(s) {1}" -f $errLines.Count, ($errLines | Select-Object -First 2) -join ' | ')
Finish
