<#
Single-player checks of Actually Working Employees against a running game in one test slot.

    pwsh -File tests/awe-check.ps1 -Slot mptest2
    pwsh -File tests/awe-check.ps1 -Slot mptest2 -Only 'opt-*,fix1-*'
    pwsh -File tests/awe-check.ps1 -List

Prints one "PASS <name>" or "FAIL <name>: <reason>" line per check (SKIP for placeholders not filled yet), a SUMMARY
line, and exits with the number of FAILs. The checks are the rows of the table in the "Check table" section: a new
check is one New-Row call. Needs a Debug build (the dev console is compiled out of Release) in a loaded save.
#>
[CmdletBinding()]
param(
    # Test slot of the running game (mptest .. mptest6). Not needed with -List.
    [string]$Slot = '',

    # Check names to run; wildcards allowed, comma separated: 'opt-*,status'. Default: all.
    [string[]]$Only = @(),

    # Print the check names and exit.
    [switch]$List,

    # Feature names (as shown by 'awe status') that must be present, e.g. -ExpectFeatures Reliability,Batching.
    [string[]]$ExpectFeatures = @(),

    # Seconds to wait for the persistence check's save reload.
    [int]$ReloadTimeoutSec = 300
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'awe-lib.ps1')

# ---- what the source says the registry holds ------------------------------------------------------------

$optionRx = 'new (Bool|Int|Float|Choice)Option\('
$groupCounts = [ordered]@{}
$optionsCs = Get-Content -LiteralPath (Join-Path $script:RepoDir 'Config\Options.cs') -Raw
if ($optionsCs -notmatch '(?s)Groups\s*\{\s*get;\s*\}\s*=\s*new\[\]\s*\{(?<g>.*?)\}') { throw 'Config/Options.cs: Groups array not found' }
foreach ($m in [regex]::Matches($Matches.g, '"(\w+)"')) {
    $file = Join-Path $script:RepoDir "Config\Options.$($m.Groups[1].Value).cs"
    $n = if (Test-Path -LiteralPath $file) { @(Select-String -LiteralPath $file -Pattern $optionRx).Count } else { 0 }
    $groupCounts[$m.Groups[1].Value] = $n
}
$totalOptions = ($groupCounts.Values | Measure-Object -Sum).Sum

# ---- regex helpers --------------------------------------------------------------------------------------

# The dev console prints "key = <value>[  (default <d>)][  host=<v>][ local][ read-only]".
function Rx-OptAt { param([string]$Key, [string]$Val) "\[Dev\]\s+$([regex]::Escape($Key)) = $Val( local| read-only)*\s*$" }
# A value equal to its default prints without the "(default ...)" suffix.
function Rx-OptSet { param([string]$Key, [string]$Val, [string]$Def) if ($Val -eq $Def) { return (Rx-OptAt $Key $Val) } "\[Dev\]\s+$([regex]::Escape($Key)) = $Val\s+\(default $Def\)" }
function Rx-Warn { param([string]$Text) "awe (set|get|reset|list): .*$Text" }

# get/set/reset steps of one option: shorter rows below.
function Step-Get   { param($Key, $Val, $Def = $null) if ($null -eq $Def) { New-Step -Cmd "awe get $Key" -Expect (Rx-OptAt $Key $Val) } else { New-Step -Cmd "awe get $Key" -Expect (Rx-OptSet $Key $Val $Def) } }
function Step-Set   { param($Key, $Raw, $Val, $Def) New-Step -Cmd "awe set $Key $Raw" -Expect (Rx-OptSet $Key $Val $Def) }
function Step-Reset { param($Key, $Val) New-Step -Cmd "awe reset $Key" -Expect (Rx-OptAt $Key $Val) }
function Step-Reject { param($Key, $Raw, $Why) New-Step -Cmd "awe set $Key $Raw" -Expect (Rx-Warn $Why) }

# -- helpers of the feature rows --

# The line 'awe status' prints for one feature: "[Dev]   <name>: <status>". Until has no anchor: the bridge matches whole log lines.
function Step-Feature {
    param([string]$Name, [string]$Status, [scriptblock]$Check = $null)
    $n = [regex]::Escape($Name)
    New-Step -Cmd 'awe status' -Until "\[Dev\]   ${n}: " -Expect "\[Dev\]   ${n}: $Status" -Forbid 'Exception' -Check $Check
}

# A dev command of a reliability fix: "[Dev] [FixN] ..." lines. Summary is the regex of the final status line. Employee is the
# regex of the per-employee lines (after the "[Dev] [FixN] " prefix); when the save has none, no such line exists and nothing
# is checked. Also lists regexes of lines a forcing mode adds; Until overrides the line the bridge waits for.
function Step-Dev {
    param([string]$Cmd, [string]$Tag, [string]$Summary, [string]$Employee = '', [string[]]$Also = @(), [string]$Until = '', [scriptblock]$Check = $null)
    $pre = "\[Dev\] \[$Tag\] "
    $wait = if ($Until) { $Until } else { $pre + $Summary }
    New-Step -Cmd $Cmd -Until $wait -Expect (@($pre + $Summary) + $Also) -Forbid 'Exception' -Check {
        param($r, $texts)
        if ($Employee) {
            foreach ($t in $texts) {
                if ($t -match ('^' + $pre) -and $t -notmatch ('^' + $pre + $Summary) -and $t -notmatch ('^' + $pre + $Employee)) {
                    return "per-employee line does not parse: $t"
                }
            }
        }
        if ($Check) { return (& $Check $r $texts) }
    }.GetNewClosure()
}

# 'awe eff <key>' prints "AWE eff <key> configured=<c> live=<l>". Returns Reason ($null when fine), Configured and Live. The
# configured value must equal what 'awe get' prints for the key; Vanilla/On/Off are mapped to vanilla/true/false first.
function Test-Eff {
    param([string]$Slot, [string]$Key)
    $out = [ordered]@{ Reason = $null; Configured = ''; Live = '' }
    $r = Invoke-Awe -Slot $Slot -Cmd "awe eff $Key" -Until 'AWE eff |awe eff:'
    if ($r.Exit -eq 1) { $out.Reason = "'awe eff $Key' failed to run: $($r.Raw -join ' | ')"; return [pscustomobject]$out }
    if ($r.ModErrors.Count -gt 0) { $out.Reason = "'awe eff $Key' logged an error: $($r.ModErrors -join ' | ')"; return [pscustomobject]$out }
    $line = $r.Texts | Where-Object { $_ -match '^AWE eff ' } | Select-Object -Last 1
    if (-not $line) { $out.Reason = "'awe eff $Key' printed no 'AWE eff' line (last lines: $(($r.Texts | Select-Object -Last 3) -join ' | '))"; return [pscustomobject]$out }
    if ($line -notmatch '^AWE eff (?<k>\S+) configured=(?<c>\S+) live=(?<l>.*)$') { $out.Reason = "unparsable eff line: $line"; return [pscustomobject]$out }
    $out.Configured = $Matches.c
    $out.Live = $Matches.l.Trim()
    if ($out.Live -match '^error:') { $out.Reason = "'awe eff $Key' live reading failed: $($out.Live)"; return [pscustomobject]$out }

    $valueRx = '^\[Dev\]\s+' + [regex]::Escape($Key) + ' = (?<v>\S+)'
    $g = Invoke-Awe -Slot $Slot -Cmd "awe get $Key" -Until ([regex]::Escape($Key) + ' = ')
    $gl = $g.Texts | Where-Object { $_ -match $valueRx } | Select-Object -Last 1
    if (-not $gl) { $out.Reason = "'awe get $Key' printed no value line"; return [pscustomobject]$out }
    [void]($gl -match $valueRx)
    $shown = $Matches.v
    $want = switch -Regex ($shown) { '^(?i)on$' { 'true' } '^(?i)off$' { 'false' } '^(?i)vanilla$' { 'vanilla' } default { $shown } }
    $have = $out.Configured
    $same = if ($want -match '^-?[\d.]+$' -and $have -match '^-?[\d.]+$') {
        [math]::Abs([double]::Parse($want, [cultureinfo]::InvariantCulture) - [double]::Parse($have, [cultureinfo]::InvariantCulture)) -lt 0.0005
    } else { $want -ieq $have }
    if (-not $same) { $out.Reason = "'awe eff $Key' says configured=$have but 'awe get' shows $shown" }
    [pscustomobject]$out
}

# Balance feature status line: "syncs=<n> <details>". A host in a loaded save has synced at least once.
$syncedCheck = {
    param($r, $texts)
    $l = $texts | Where-Object { $_ -match 'syncs=(\d+)' } | Select-Object -Last 1
    [void]($l -match 'syncs=(\d+)')
    if ([int]$Matches[1] -lt 1) { 'syncs=0, the feature never ran (is this the host?)' }
}

# ---- Check table ----------------------------------------------------------------------------------------
# Order matters only inside a row. Rows that change an option clean up in -Finally.

$rows = New-Object System.Collections.Generic.List[hashtable]

# -- boot --
$rows.Add((New-Row 'mod-loaded' -Steps @(
    New-Step -Log 'Actually Working Employees v' -Expect 'Actually Working Employees v\d+\.\d+\.\d+\S*.*\b\d+ options' -Check {
        param($r, $texts)
        $line = $texts | Where-Object { $_ -match 'Actually Working Employees v\S+.*?(\d+) options' } | Select-Object -Last 1
        [void]($line -match '(\d+) options')
        if ([int]$Matches[1] -ne $totalOptions) { "reports $($Matches[1]) options, the source declares $totalOptions" }
    })))
$rows.Add((New-Row 'options-registered' -Steps @(
    New-Step -Log '[Options]' -Expect "\[Options\] $totalOptions options in $($groupCounts.Count) groups" `
        -Forbid 'has unknown group', 'duplicate key', 'is missing', 'failed')))
$rows.Add((New-Row 'features-boot' -Steps @(
    New-Step -Log '[Features]' -Expect '\[Features\] \d+ active')))

# -- dev console --
$rows.Add((New-Row 'awehelp' -Steps @(
    New-Step -Cmd 'awehelp' -Until 'Groups: ' -Expect 'awe help', 'awe list \[group\]', 'awe get <key>', 'awe set <key> <value>',
        'awe reset <key\|all>', 'awe status', ('Groups: ' + ($groupCounts.Keys -join ', ')))))
$rows.Add((New-Row 'awe-help' -Steps @(
    New-Step -Cmd 'awe help' -Until 'Groups: ' -Expect 'awe list \[group\]', 'awe status')))
$rows.Add((New-Row 'list-count' -Steps @(
    New-Step -Cmd 'awe list' -Until '\d+ option\(s\)\.' -Expect "\[Dev\] $totalOptions option\(s\)\." -Check {
        param($r, $texts)
        $n = @($texts | Where-Object { $_ -match '^\[Dev\]   \w+\.\w+ = ' }).Count
        if ($n -ne $totalOptions) { "printed $n option lines, expected $totalOptions" }
    })))
foreach ($g in $groupCounts.Keys) {
    $rows.Add((New-Row "list-group-$($g.ToLowerInvariant())" -Steps @(
        New-Step -Cmd "awe list $g" -Until '\d+ option\(s\)\.' -Expect "\[Dev\] $($groupCounts[$g]) option\(s\)\." -Check {
            param($r, $texts)
            $bad = @($texts | Where-Object { $_ -match '^\[Dev\]   (\w+)\.\w+ = ' -and $Matches[1] -ne $g.ToLowerInvariant() })
            if ($bad.Count -gt 0) { "lists options of another group: $($bad[0])" }
        }.GetNewClosure())))
}
$rows.Add((New-Row 'list-bad-group' -Steps @(New-Step -Cmd 'awe list nosuchgroup' -Expect "no group 'nosuchgroup'")))
$rows.Add((New-Row 'console-errors' -Steps @(
    New-Step -Cmd 'awe get nosuchkey' -Expect "awe get: no option 'nosuchkey'"
    New-Step -Cmd 'awe set nosuchkey 1' -Expect "awe set: no option 'nosuchkey'"
    New-Step -Cmd 'awe reset nosuchkey' -Expect "awe reset: no option 'nosuchkey'"
    New-Step -Cmd 'awe nosuchsub' -Expect "unknown command 'awe nosuchsub'"
    New-Step -Cmd 'awe get' -Expect 'awe get <key>'
    New-Step -Cmd 'awe set general.verboselogging' -Expect 'awe set <key> <value>')))
$rows.Add((New-Row 'key-ignores-case' -Steps @(New-Step -Cmd 'awe get BOTANIST.MaxPots' -Expect '\[Dev\]\s+botanist\.maxpots = ')))

# -- option round trips: one per type, plus the -1 vanilla sentinel --
$k = 'general.verboselogging'
$rows.Add((New-Row 'opt-bool' -Finally "awe reset $k" -Steps @(
    Step-Reset $k 'Off'
    Step-Set $k 'true' 'On' 'Off'
    Step-Get $k 'On' 'Off'
    New-Step -Cmd "awe set $k off" -Expect (Rx-OptAt $k 'Off')
    Step-Reject $k 'maybe' 'expected on or off'
    Step-Get $k 'Off'
    Step-Set $k 'yes' 'On' 'Off'
    Step-Reset $k 'Off')))

$k = 'botanist.maxpots'
$rows.Add((New-Row 'opt-int' -Finally "awe reset $k" -Steps @(
    Step-Reset $k '8'
    Step-Set $k '12' '12' '8'
    Step-Get $k '12' '8'
    Step-Reject $k '0' 'outside 1-32'
    Step-Reject $k '33' 'outside 1-32'
    Step-Reject $k 'abc' 'expected a whole number'
    Step-Reject $k '1.5' 'expected a whole number'
    Step-Get $k '12' '8'
    Step-Set $k '32' '32' '8'
    Step-Set $k '1' '1' '8'
    Step-Reset $k '8')))

$k = 'botanist.dailywage'
$rows.Add((New-Row 'opt-int-vanilla' -Finally "awe reset $k" -Steps @(
    Step-Reset $k 'Vanilla'
    Step-Set $k '500' '500' 'Vanilla'
    New-Step -Cmd "awe set $k -1" -Expect (Rx-OptAt $k 'Vanilla')
    Step-Reject $k '-2' 'outside 0-10000 \(or -1 for vanilla\)'
    Step-Reject $k '10001' 'outside 0-10000'
    Step-Set $k '0' '0' 'Vanilla'
    New-Step -Cmd "awe set $k vanilla" -Expect (Rx-OptAt $k 'Vanilla')
    Step-Reset $k 'Vanilla')))

$k = 'botanist.walkspeed'
$rows.Add((New-Row 'opt-float' -Finally "awe reset $k" -Steps @(
    Step-Reset $k 'Vanilla'
    Step-Set $k '2.5' '2.5' 'Vanilla'
    Step-Get $k '2.5' 'Vanilla'
    Step-Reject $k '9' 'outside 0.5-6 \(or -1 for vanilla\)'
    Step-Reject $k '0.1' 'outside 0.5-6'
    Step-Reject $k 'abc' 'expected a number'
    Step-Get $k '2.5' 'Vanilla'
    Step-Set $k '0.5' '0.5' 'Vanilla'
    Step-Set $k '6' '6' 'Vanilla'
    New-Step -Cmd "awe set $k -1" -Expect (Rx-OptAt $k 'Vanilla')
    Step-Reset $k 'Vanilla')))

$k = 'packager.routeprioritymode'
$rows.Add((New-Row 'opt-choice' -Finally "awe reset $k" -Steps @(
    Step-Reset $k 'Vanilla'
    Step-Set $k 'fullest' 'Fullest' 'Vanilla'
    Step-Get $k 'Fullest' 'Vanilla'
    Step-Reject $k 'bogus' 'expected one of Vanilla, Fullest, Emptiest, NearestFirst'
    Step-Get $k 'Fullest' 'Vanilla'
    Step-Set $k 'NEARESTFIRST' 'NearestFirst' 'Vanilla'
    Step-Reset $k 'Vanilla')))

$rows.Add((New-Row 'reset-all' -Finally 'awe reset all' -Steps @(
    New-Step -Cmd 'awe set botanist.maxpots 9' -Expect 'botanist\.maxpots = 9'
    New-Step -Cmd 'awe set general.verboselogging on' -Expect 'general\.verboselogging = On'
    New-Step -Cmd 'awe reset all' -Expect 'awe reset all: (\d+) option\(s\) reset\.' -Check {
        param($r, $texts)
        $line = $texts | Where-Object { $_ -match 'awe reset all: (\d+) option' } | Select-Object -Last 1
        [void]($line -match 'awe reset all: (\d+)')
        if ([int]$Matches[1] -lt 2) { "reset only $($Matches[1]) option(s), two were changed" }
    }
    Step-Get 'botanist.maxpots' '8'
    Step-Get 'general.verboselogging' 'Off'
    New-Step -Cmd 'awe status' -Expect 'changed=0\b')))

# -- preset: "Fast" writes work speed x2 and walk speed 6 for all four roles once; Reset all takes it back --
$fastKeys = @('botanist.workspeed', 'chemist.workspeed', 'packager.workspeed', 'cleaner.workspeed')
$fastWalk = @('botanist.walkspeed', 'chemist.walkspeed', 'packager.walkspeed', 'cleaner.walkspeed')
$rows.Add((New-Row 'preset-fast' -Finally 'awe reset all' -Steps (@(
    New-Step -Cmd 'awe reset all' -Expect 'awe reset all: \d+ option\(s\) reset\.'
    New-Step -Cmd 'awe preset fast' -Until 'awe preset' -Expect 'awe preset fast: 8 of 8 option\(s\) changed, 0 rejected\.' -Forbid 'Exception'
) + @($fastKeys | ForEach-Object { Step-Get $_ '2' '1' }) + @($fastWalk | ForEach-Object { Step-Get $_ '6' 'Vanilla' }) + @(
    New-Step -Cmd 'awe get botanist.dailywage' -Expect (Rx-OptAt 'botanist.dailywage' 'Vanilla')
    New-Step -Cmd 'awe preset fast' -Until 'awe preset' -Expect 'awe preset fast: 0 of 8 option\(s\) changed, 0 rejected\.'
    New-Step -Cmd 'awe preset bogus' -Until 'awe preset' -Expect "awe preset: no preset 'bogus'\. Presets: fast"
    New-Step -Cmd 'awe status' -Expect 'changed=8\b'
    New-Step -Cmd 'awe reset all' -Expect 'awe reset all: 8 option\(s\) reset\.'
) + @($fastKeys | ForEach-Object { Step-Get $_ '1' }) + @($fastWalk | ForEach-Object { Step-Get $_ 'Vanilla' }))))

# -- status:every feature the boot log announced is listed with its state --
$rows.Add((New-Row 'status' -Steps @(
    New-Step -Cmd 'awe status' -Until 'features=\d+' -Expect "options=$totalOptions changed=\d+" -Check {
        param($r, $texts)
        $m = $null
        foreach ($t in $texts) { if ($t -match '^\[Dev\] role=(\S+) inWorld=(\w+) options=(\d+) changed=(\d+) vanillaBalance=(\w+) features=(\d+)') { $m = $Matches } }
        if (-not $m) { return 'no status header line' }
        if ($m[2] -ne 'True') { return "inWorld=$($m[2]), the save is not loaded" }
        $names = @($texts | Where-Object { $_ -match '^\[Dev\]   (\S[^:]*): ' } | ForEach-Object { $Matches[1] })
        if ($names.Count -ne [int]$m[6]) { return "header says features=$($m[6]) but $($names.Count) feature lines follow" }
        $boot = (Get-LogLines -Slot $Slot -Grep '[Features]').Lines | Where-Object { $_ -match '\[Features\] (\d+) active: ?(.*)$' } | Select-Object -Last 1
        if (-not $boot) { return 'no [Features] line in the boot log to compare with' }
        [void]($boot -match '\[Features\] (\d+) active: ?(.*)$')
        if ([int]$Matches[1] -ne $names.Count) { return "boot log says $($Matches[1]) features, status lists $($names.Count)" }
        foreach ($n in ($Matches[2] -split ',\s*' | Where-Object { $_ })) { if ($names -notcontains $n) { return "feature '$n' is in the boot log but not in the status" } }
        foreach ($n in $ExpectFeatures) { if ($names -notcontains $n) { return "expected feature '$n' is not listed (listed: $($names -join ', '))" } }
    })))

# -- persistence: a value survives a save and a reload of the save in the same game --
# A full restart is covered by awe-prefs-persist.ps1; this proves the value and the feature state are not dropped by the
# save/load cycle (ResetState on save start must not touch the options).
$rows.Add((New-Row 'persist-reload' -Finally 'awe reset botanist.maxpots' -Run {
    param($Slot)
    $key = 'botanist.maxpots'
    $h = Get-Health $Slot
    if (-not $h.inGame) { return 'no save is loaded' }
    $saveSlot = $h.saveSlot
    $why = Test-Step -Slot $Slot -Step (Step-Set $key '13' '13' '8')
    if ($why) { return "set: $why" }

    $save = Invoke-Bridge $Slot 'save_now'
    if (-not $save.ok) { return "save_now answered $($save | ConvertTo-Json -Compress)" }
    $menu = Invoke-Bridge $Slot 'return_to_menu'
    if (-not $menu.ok) { return "return_to_menu answered $($menu | ConvertTo-Json -Compress)" }
    # inGame turns false when the menu scene starts loading; load_save answers BUSY until that load is over, which takes
    # longer with more mods in the slot.
    $why = Wait-Until -TimeoutSec 90 -What 'the main menu (scene Menu, inGame false, loading false)' -Condition {
        try { $mh = Get-Health $Slot; $mh.scene -eq 'Menu' -and -not $mh.inGame -and -not $mh.loading }
        catch { $script:LastPollError = $_.Exception.Message; $false }
    }
    if ($why) { return $why }

    # load_save completes once the load has started, up to 60 s later when a mod holds StartGame back.
    $load = Invoke-PyBackground -What "load_save $saveSlot" -TimeoutSec 100 `
        -PyArgs @($script:BridgePy, '--slot', $Slot, '--timeout-ms', '90000', 'call', 'load_save', "{`"slot`": $saveSlot}")
    if ($load.ExitCode -ne 0) { return "load_save slot $saveSlot failed: $($load.Lines -join ' | ')" }
    $why = Wait-GameReady -Slot $Slot -TimeoutSec $ReloadTimeoutSec
    if ($why) { return $why }

    $why = Test-Step -Slot $Slot -Step (Step-Get $key '13' '8')
    if ($why) { return "after the reload: $why" }
    $null
}))

# -- reliability fixes: the dev command runs, prints its summary line, and the feature is on in 'awe status' --
# The test save may have no employees. Then only the summary line exists (and a forcing mode answers with its "no employee"
# warning); per-employee lines are parsed only when there are any.
$rows.Add((New-Row 'fix1-standstill' -Steps @(
    Step-Feature 'fix1-standstill' 'on=11 checks=\d+ warps=\d+ gaveup=\d+ partial=\d+ partialwarps=\d+'
    Step-Dev 'awe standstill' 'Fix1' 'on=11 checks=\d+ warps=\d+ gaveup=\d+ partial=\d+ partialwarps=\d+' `
        '.+: behaviour=\S+ work=\w+ moving=\w+ distToDest=\S+ stillFor=\S+s tries=\d+ partial=\d+'
    Step-Dev 'awe standstill warp' 'Fix1' 'on=11 checks=\d+ warps=\d+ gaveup=\d+ partial=\d+ partialwarps=\d+' `
        -Until '\[Fix1\] warped .+ \(dev command\)|awe standstill warp: no employee is on a work task' `
        -Also '\[Fix1\] warped .+ \(dev command\)|awe standstill warp: no employee is on a work task'
    Step-Dev 'awe standstill partial' 'Fix1' 'on=11 checks=\d+ warps=\d+ gaveup=\d+ partial=\d+ partialwarps=\d+' `
        -Until '\[Fix1\] warped .+ \(partial walk x\d+\)|awe standstill partial: no employee is on a work task' `
        -Also '\[Fix1\] warped .+ \(partial walk x\d+\)|awe standstill partial: no employee is on a work task')))

$chemEmp = '.+: cauldronActive=\w+ isAnyWorkInProgress=\w+ active=\S+ enabledWork=\d+ idleFor=\S+s'
$chemSum = '\d+ chemist\(s\)\. on=1 cauldronWork=\d+ idleResets=\d+'
$rows.Add((New-Row 'fix2-chemist-work' -Steps @(
    Step-Feature 'fix2-chemist-work' 'on=1 cauldronWork=\d+ idleResets=\d+'
    Step-Dev 'awe chemwork' 'Fix2' $chemSum $chemEmp
    Step-Dev 'awe chemwork reset' 'Fix2' $chemSum $chemEmp)))

$lockEmp = '.+: \d+ station lock\(s\), \d+ slot lock\(s\)( released \(dev command\))? active=\S+'
$lockStat = 'on=11 sweeps=\d+ stationLocks=\d+ slotLocks=\d+ sleepReleases=\d+'
$rows.Add((New-Row 'fix3-lock-leak' -Steps @(
    Step-Feature 'fix3-lock-leak' $lockStat
    Step-Dev 'awe lockleak' 'Fix3' ('\d+ placed item\(s\), \d+ lock\(s\) held\. ' + $lockStat) $lockEmp
    Step-Dev 'awe lockleak release' 'Fix3' ('\d+ placed item\(s\), \d+ lock\(s\) released\. ' + $lockStat) $lockEmp)))

$stuckEmp = '.+: carries \d+( \(.+\))? mover=(none|active=\w+ grabbed=\S+)'
$stuckStat = 'on=1 inspected=\d+ returned=\d+ sourceFull=\d+'
$rows.Add((New-Row 'fix4-stuck-item' -Steps @(
    Step-Feature 'fix4-stuck-item' $stuckStat
    Step-Dev 'awe stuckitem' 'Fix4' $stuckStat $stuckEmp
    Step-Dev 'awe stuckitem cut' 'Fix4' $stuckStat $stuckEmp)))

$reasonStat = 'on=1 rewritten=\d+ kept=\d+'
$rows.Add((New-Row 'fix5-clear-reasons' -Steps @(
    Step-Feature 'fix5-clear-reasons' $reasonStat
    Step-Dev 'awe reasons' 'Fix5' ('\d+ employee\(s\)\. ' + $reasonStat) '.+'
    Step-Dev 'awe reasons submit' 'Fix5' ('\d+ employee\(s\)\. ' + $reasonStat) '.+')))

# The cache test asks the same path twice: one miss, then one hit. Without employees there is no test line and the counters
# only have to parse.
$reachStat = 'on=1 hits=\d+ misses=\d+ entries=\d+'
$reachTest = {
    param($r, $texts)
    $t = $texts | Where-Object { $_ -match '\[Fix6\] .+: CanGetTo twice -> ' } | Select-Object -Last 1
    if (-not $t) { Write-Host '  NOTE fix6-reach-cache: no employee in the save, the hit/miss counters were not exercised'; return }
    if ($t -notmatch 'CanGetTo twice -> (?<a>\w+)/(?<b>\w+), hits \+(?<h>\d+), misses \+(?<m>\d+)') { return "test line does not parse: $t" }
    if ($Matches.a -ne $Matches.b) { return "the same path answered $($Matches.a) then $($Matches.b)" }
    if ([int]$Matches.h -lt 1) { return "second CanGetTo was no cache hit (hits +$($Matches.h), misses +$($Matches.m)); is the cache on and this the host?" }
    if (([int]$Matches.h + [int]$Matches.m) -lt 2) { return "the two CanGetTo calls moved the counters by $([int]$Matches.h + [int]$Matches.m), expected 2: $t" }
    $s = $texts | Where-Object { $_ -match '\[Fix6\] on=1 hits=(\d+)' } | Select-Object -Last 1
    [void]($s -match 'hits=(\d+)')
    if ([int]$Matches[1] -lt 1) { 'status hits=0 after a counted hit' }
}
$rows.Add((New-Row 'fix6-reach-cache' -Steps @(
    Step-Feature 'fix6-reach-cache' $reachStat
    Step-Dev 'awe reach' 'Fix6' $reachStat
    Step-Dev 'awe reach clear' 'Fix6' $reachStat -Also '\[Dev\] \[Fix6\] cleared \d+ cached path check\(s\)\.'
    Step-Dev 'awe reach test' 'Fix6' $reachStat -Check $reachTest)))

# -- balance: feature line with its details, and 'awe eff' for options that have a live reading --
# A live value is "not seen yet" until the game called the patched method with the option changed; the NOTE line records
# which of the two the run saw, so a patch the JIT inlined away shows up as "not seen yet" while an object exists.
function Row-Balance {
    param([string]$Name, [string]$Feature, [string]$Details, [string[]]$Keys)
    # Built from text, not with GetNewClosure: a closure cannot see this script's functions.
    $keyList = ($Keys | ForEach-Object { "'$_'" }) -join ', '
    $body = @'
param($Slot)
$why = Test-Step -Slot $Slot -Step (Step-Feature '@@FEATURE@@' 'syncs=\d+ @@DETAILS@@' $syncedCheck)
if ($why) { return $why }
foreach ($k in @(@@KEYS@@)) {
    $e = Test-Eff $Slot $k
    if ($e.Reason) { return $e.Reason }
    Write-Host "  NOTE @@NAME@@: $k configured=$($e.Configured) live=$($e.Live)"
}
$null
'@
    $body = $body.Replace('@@FEATURE@@', $Feature).Replace('@@DETAILS@@', $Details).Replace('@@KEYS@@', $keyList).Replace('@@NAME@@', $Name)
    New-Row $Name -Run ([scriptblock]::Create($body))
}
$rows.Add((Row-Balance 'bal-botanist' 'bal-botanist' 'botanists=\d+' @('botanist.maxpots', 'botanist.dailywage', 'botanist.walkspeed')))
$rows.Add((Row-Balance 'bal-chemist' 'bal-chemist' 'chemists=\d+' @('chemist.maxstations', 'chemist.dailywage')))
$rows.Add((Row-Balance 'bal-packager' 'bal-packager' 'packagers=\d+' @('packager.maxstations')))
$rows.Add((Row-Balance 'bal-cleaner' 'bal-cleaner' 'cleaners=\d+' @('cleaner.maxbins', 'cleaner.dailywage')))
$rows.Add((Row-Balance 'bal-property' 'bal-property' 'properties=\d+' @('property.employeecapacity', 'property.enableemployeecapacityoverride')))

# Stations: the live reading of the drying rack and the lab oven comes from the patched game methods. Read at the default,
# then with the speed changed to 2 (and the station-speed switch on), so both outcomes are recorded.
$rows.Add((New-Row 'bal-stations' -Finally 'awe reset stations.dryingrackspeed', 'awe reset stations.labovenspeed', 'awe reset stations.enablestationspeed' -Run {
    param($Slot)
    $why = Test-Step -Slot $Slot -Step (Step-Feature 'bal-stations' 'syncs=\d+ pots=\d+ colonies=\d+ cauldrons=\d+ mixers=\d+' $syncedCheck)
    if ($why) { return $why }
    $keys = 'stations.dryingrackspeed', 'stations.labovenspeed'
    foreach ($k in $keys) {
        $e = Test-Eff $Slot $k
        if ($e.Reason) { return $e.Reason }
        Write-Host "  NOTE bal-stations: $k at default configured=$($e.Configured) live=$($e.Live)"
    }
    foreach ($s in @(
            (New-Step -Cmd 'awe set stations.enablestationspeed true' -Expect 'stations\.enablestationspeed = On' -Forbid 'Exception'),
            (New-Step -Cmd 'awe set stations.dryingrackspeed 2' -Expect 'stations\.dryingrackspeed = 2\b' -Forbid 'Exception'),
            (New-Step -Cmd 'awe set stations.labovenspeed 2' -Expect 'stations\.labovenspeed = 2\b' -Forbid 'Exception'))) {
        $why = Test-Step -Slot $Slot -Step $s
        if ($why) { return $why }
    }
    # The balance feature re-applies once a second: poll every 0.5 s until both keys read 2, at most 10 s. The checks below
    # then report PASS or the failing key.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($true) {
        $ready = $true
        foreach ($k in $keys) {
            $e = Test-Eff $Slot $k
            if ($e.Reason -or $e.Configured -ne '2') { $ready = $false; break }
        }
        if ($ready -or [DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Milliseconds 500
    }
    foreach ($k in $keys) {
        $e = Test-Eff $Slot $k
        if ($e.Reason) { return $e.Reason }
        if ($e.Configured -ne '2') { return "$k configured=$($e.Configured) after setting 2" }
        $seen = if ($e.Live -eq 'not seen yet') { 'not seen yet (no rack/oven called the patched method, or the patch is inlined away)' } else { "seen: $($e.Live)" }
        Write-Host "  NOTE bal-stations: $k set to 2, live=$seen"
    }
    $null
}))

# Fix 7 and fix 8: the status keys parse; the option is set to a new value and 'awe eff' must show it as configured.
$rows.Add((New-Row 'fix7-packager-routes' -Finally 'awe reset packager.maxroutes' -Run {
    param($Slot)
    $key = 'packager.maxroutes'
    $why = Test-Step -Slot $Slot -Step (Step-Feature 'fix7-packager-routes' 'syncs=\d+ packagers=\d+ writes=\d+' $syncedCheck)
    if ($why) { return $why }
    $e = Test-Eff $Slot $key
    if ($e.Reason) { return $e.Reason }
    if ($e.Live -notmatch '^(\d+|no packager)$') { return "live reading '$($e.Live)' is neither a number nor 'no packager'" }
    $why = Test-Step -Slot $Slot -Step (Step-Set $key '7' '7' '5')
    if ($why) { return $why }
    $e = Test-Eff $Slot $key
    if ($e.Reason) { return $e.Reason }
    if ($e.Configured -ne '7') { return "configured=$($e.Configured) after setting 7" }
    if ($e.Live -match '^\d+$') {
        $why = Wait-Until -TimeoutSec 8 -What "the live route limit to reach 7" -Condition { (Test-Eff $Slot $key).Live -eq '7' }
        if ($why) { return "$why (a packager exists, the limit was not written)" }
        Write-Host '  NOTE fix7-packager-routes: live route limit followed the option on a hired packager'
    }
    else { Write-Host "  NOTE fix7-packager-routes: live=$($e.Live), no packager hired; only the configured value was checked" }
    $null
}))

$rows.Add((New-Row 'fix8-harvest-time' -Finally 'awe reset botanist.harvesttimeperunit', 'awe reset botanist.mushroomharvesttimeperunit' -Run {
    param($Slot)
    $why = Test-Step -Slot $Slot -Step (Step-Feature 'fix8-harvest-time' 'syncs=\d+ pot=[\d.]+s/unit mushroom=[\d.]+s/unit' $syncedCheck)
    if ($why) { return $why }
    foreach ($k in 'botanist.harvesttimeperunit', 'botanist.mushroomharvesttimeperunit') {
        $e = Test-Eff $Slot $k
        if ($e.Reason) { return $e.Reason }
        if ($e.Live -notmatch '^(not seen yet|[\d.]+ s)$') { return "$k live reading '$($e.Live)' is neither 'not seen yet' nor '<n> s'" }
        $why = Test-Step -Slot $Slot -Step (New-Step -Cmd "awe set $k 2.5" -Expect ([regex]::Escape($k) + ' = 2\.5') -Forbid 'Exception')
        if ($why) { return $why }
        $e = Test-Eff $Slot $k
        if ($e.Reason) { return $e.Reason }
        if ($e.Configured -ne '2.5') { return "$k configured=$($e.Configured) after setting 2.5" }
        Write-Host "  NOTE fix8-harvest-time: $k set to 2.5, live=$($e.Live)"
    }
    $why = Test-Step -Slot $Slot -Step (Step-Feature 'fix8-harvest-time' 'syncs=\d+ pot=2\.5s/unit mushroom=2\.5s/unit')
    if ($why) { return "status after the change: $why" }
    $null
}))

# -- co-op sync, single player: the report prints without an error; a host without a guest has no value on the lobby key --
$rows.Add((New-Row 'coop-sync' -Steps @(
    Step-Feature 'coop-sync' 'role=(single-player|host) bytes=\d+ wire=\d+ writes=\d+ readback_fail=0 applied=\d+ hash=\S+'
    New-Step -Cmd 'awe sync' -Until 'AWE sync (decoded|no value|decode failed)' -Forbid 'Exception', 'decode failed' `
        -Expect 'AWE sync role=(single-player|host) key=awe_opts bytes=\d+ hash=\S+ writes=\d+ readback_fail=0 applied=\d+ inLobby=\w+',
            'AWE sync (decoded \d+ option\(s\), hash \S+: |no value on the lobby key)')))

# -- phone app. The dev console has no command that closes the phone; 'awe app open' leaves it raised. --
$rows.Add((New-Row 'app-phone' -Steps @(
    New-Step -Log '[App] registered with Sideload' -Expect "\[App\] registered with Sideload as 'awe'\." -Forbid 'Sideload is not loaded yet'
    Step-Feature 'PhoneApp' 'registered, sideload present, page (open|closed)'
    New-Step -Cmd 'awe app dump' -Until 'awe app dump: ' -Forbid 'Exception' `
        -Expect "awe app dump: $($groupCounts.Count) groups, $totalOptions options rendered, 0 locked, role=(single-player|host), \d+ chars\."
    New-Step -Cmd 'awe app open' -Until 'awe app open: ' -Forbid 'Exception', 'refused to raise the phone' `
        -Expect "awe app open: phone raised, app 'awe' opened\.")))

# -- rows of single features, one file per area: tests/rows/<area>.ps1 adds rows with $rows.Add((New-Row ...)) --
foreach ($f in (Get-ChildItem (Join-Path $PSScriptRoot 'rows') -Filter *.ps1 -ErrorAction SilentlyContinue | Sort-Object Name)) {
    try { . $f.FullName }
    catch { Write-Host "FAIL rows-load: $($f.Name): $($_.Exception.Message)" }
}

# -- last: nothing in the whole run threw --
$rows.Add((New-Row 'log-no-failed' -Steps @(
    New-Step -Log '[Failed]' -Forbid '\S'
    New-Step -Log '[Options] reset' -Forbid 'failed')))

# ---- run ------------------------------------------------------------------------------------------------

if ($List) { $rows | ForEach-Object { $_.Name }; exit 0 }
if (-not $Slot) { Write-Host 'FAIL setup: -Slot is required'; exit 1 }

$patterns = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$selected = @($rows | Where-Object {
    $name = $_.Name
    ($patterns.Count -eq 0) -or [bool]($patterns | Where-Object { $name -like $_ })
})
if ($selected.Count -eq 0) { Write-Host "FAIL setup: -Only '$($Only -join ',')' matches no check (see -List)"; exit 1 }

$notReady = Wait-GameReady -Slot $Slot -TimeoutSec 60
if ($notReady) { Write-Result 'game-ready' $notReady; Write-Host "SUMMARY slot=$Slot pass=0 fail=1 skip=$($selected.Count) (no game, nothing ran)"; exit 1 }

foreach ($row in $selected) {
    if ($row.Pending) { Write-Result $row.Name 'placeholder, no steps yet' -Skip; continue }
    Write-Result $row.Name (Test-Row -Slot $Slot -Row $row)
}
Write-Host "SUMMARY slot=$Slot pass=$($script:Passed) fail=$($script:Failed) skip=$($script:Skipped)"
exit $script:Failed
