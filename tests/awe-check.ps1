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
function Rx-OptSet { param([string]$Key, [string]$Val, [string]$Def) "\[Dev\]\s+$([regex]::Escape($Key)) = $Val\s+\(default $Def\)" }
function Rx-Warn { param([string]$Text) "awe (set|get|reset|list): .*$Text" }

# get/set/reset steps of one option: shorter rows below.
function Step-Get   { param($Key, $Val, $Def = $null) if ($null -eq $Def) { New-Step -Cmd "awe get $Key" -Expect (Rx-OptAt $Key $Val) } else { New-Step -Cmd "awe get $Key" -Expect (Rx-OptSet $Key $Val $Def) } }
function Step-Set   { param($Key, $Raw, $Val, $Def) New-Step -Cmd "awe set $Key $Raw" -Expect (Rx-OptSet $Key $Val $Def) }
function Step-Reset { param($Key, $Val) New-Step -Cmd "awe reset $Key" -Expect (Rx-OptAt $Key $Val) }
function Step-Reject { param($Key, $Raw, $Why) New-Step -Cmd "awe set $Key $Raw" -Expect (Rx-Warn $Why) }

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

# -- status: every feature the boot log announced is listed with its state --
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
# MelonPreferences are written at shutdown only, so a full restart is not covered here; this proves the value and the
# feature state are not dropped by the save/load cycle (ResetState on save start must not touch the options).
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
    $why = Wait-Until -TimeoutSec 90 -What 'the main menu (inGame false)' -Condition {
        try { -not (Get-Health $Slot).inGame } catch { $script:LastPollError = $_.Exception.Message; $false }
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

# -- features of this release: the lead fills the Steps of each placeholder (New-Row 'name' -Steps @(New-Step ...)) --
foreach ($n in 'fix1-todo', 'fix2-todo', 'fix3-todo', 'fix4-todo', 'fix5-todo', 'fix6-todo', 'fix7-packager-routes', 'fix8-harvest-time', 'bal-todo', 'coop-sync', 'app-todo') {
    $rows.Add((New-Row $n -Pending))
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
