<#
Co-op sync checks of Actually Working Employees on a joined host/client pair.

    pwsh -File tests/awe-coop.ps1 -HostSlot mptest2 -ClientSlot mptest3
    pwsh -File tests/awe-coop.ps1 -HostSlot mptest2 -ClientSlot mptest3 -Only 'sync-*'

The lead starts the pair first (session.ps1 -Coop ...) and both games must be in the world with the guest in the host's
lobby. Prints PASS/FAIL per check, a SUMMARY line and exits with the number of FAILs. Needs a Debug build on both sides.
The host writes its options once a guest is in the lobby, polls every 2 s, and the client polls every 2 s, so every
wait below polls the client for the value itself, up to -SyncTimeoutSec.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HostSlot,
    [Parameter(Mandatory)][string]$ClientSlot,
    [string[]]$Only = @(),
    [int]$SyncTimeoutSec = 30
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'awe-lib.ps1')

# Host-only options of each type: key, value to set, how the dev console describes it, its default text.
$cases = @(
    @{ Key = 'botanist.maxpots';               Set = '20';      Shown = '20';      Default = '8' }
    @{ Key = 'botanist.walkspeed';             Set = '2.5';     Shown = '2.5';     Default = 'Vanilla' }
    @{ Key = 'packager.routeprioritymode';     Set = 'Fullest'; Shown = 'Fullest'; Default = 'Vanilla' }
    @{ Key = 'general.disableemployeecollision'; Set = 'true';  Shown = 'On';      Default = 'Off' }
)

# The client's 'awe get' line for a key, polled until $Test accepts it. Returns $null when it did, else the reason.
function Wait-ClientLine {
    param([string]$Key, [scriptblock]$Test, [string]$What)
    $last = ''
    $why = Wait-Until -TimeoutSec $SyncTimeoutSec -What $What -Condition {
        $r = Invoke-Awe -Slot $ClientSlot -Cmd "awe get $Key" -Until ([regex]::Escape($Key) + ' = ') -MaxWaitMs 6000
        $line = $r.Texts | Where-Object { $_ -match ('\[Dev\]\s+' + [regex]::Escape($Key) + ' = ') } | Select-Object -Last 1
        $script:LastLine = "$line"
        [bool]($line -and (& $Test $line))
    }
    if ($why) { return "$why; last client line: '$script:LastLine'" }
    $null
}

# Parses 'AWE sync ...' and the decoded line of one slot. Returns a hashtable, or a string with the reason.
function Get-Sync {
    param([string]$Slot)
    $r = Invoke-Awe -Slot $Slot -Cmd 'awe sync' -Until 'AWE sync decoded' -MaxWaitMs 8000
    if ($r.Exit -eq 1) { return "awe sync failed to run on ${Slot}: $($r.Raw -join ' | ')" }
    $head = $r.Texts | Where-Object { $_ -match '^AWE sync role=' } | Select-Object -Last 1
    if (-not $head) { return "no 'AWE sync role=' line on $Slot (last lines: $(($r.Texts | Select-Object -Last 3) -join ' | '))" }
    if ($head -notmatch 'role=(?<role>\S+) key=(?<key>\S+) bytes=(?<bytes>\d+) hash=(?<hash>\S+) writes=(?<writes>\d+) readback_fail=(?<rf>\d+) applied=(?<applied>\d+) inLobby=(?<lobby>\w+)') {
        return "unparsable sync line on ${Slot}: $head"
    }
    $s = @{ Role = $Matches.role; Key = $Matches.key; Hash = $Matches.hash; ReadbackFail = [int]$Matches.rf; InLobby = $Matches.lobby; Head = $head; Decoded = ''
            Writes = [int]$Matches.writes; Applied = [int]$Matches.applied }
    $dec = $r.Texts | Where-Object { $_ -match '^AWE sync decoded (\d+) option' } | Select-Object -Last 1
    if ($dec) { $s.Decoded = $dec }
    $s
}

# Publishes on the host and applies on the client, from the 'awe sync' counters. The matching [CoopSync] log lines are
# throttled to one per 10 s (OptionSync.Info), so counting them misses changes that come close together.
function Get-SyncCounts {
    $h = Get-Sync $HostSlot;    if ($h -is [string]) { return $h }
    $cl = Get-Sync $ClientSlot; if ($cl -is [string]) { return $cl }
    @{ Writes = $h.Writes; Applied = $cl.Applied }
}

# Both sides' sync lines and the host's last guest line, appended to a sync timeout so the failure shows which side stopped.
function Get-HostState {
    $h = Get-Sync $HostSlot
    $hs = if ($h -is [string]) { $h } else { $h.Head }
    $cl = Get-Sync $ClientSlot
    $cs = if ($cl -is [string]) { $cl } else { $cl.Head }
    $guest = (Get-LogLines -Slot $HostSlot -Grep 'guest' -Count 5).Lines | Where-Object { $_ -match 'guest present|no guest' } | Select-Object -Last 1
    "host now: '$hs'; client now: '$cs'; host guest line: '$guest'"
}

function Count-CoopLines {
    param([string]$Slot, [string]$Fragment)
    $r = Get-LogLines -Slot $Slot -Grep '[CoopSync]' -Count 2000
    @($r.Lines | Where-Object { $_ -like "*$Fragment*" }).Count
}

function Reset-Both {
    Invoke-Awe -Slot $HostSlot -Cmd 'awe reset all' -Until 'reset all' | Out-Null
    Invoke-Awe -Slot $ClientSlot -Cmd 'awe reset all' -Until 'reset all' | Out-Null
}

$rows = New-Object System.Collections.Generic.List[hashtable]

$rows.Add((New-Row 'roles' -Run {
    param($Slot)
    foreach ($pair in @(@($HostSlot, 'host'), @($ClientSlot, 'client'))) {
        $why = Test-Step -Slot $pair[0] -Step (New-Step -Cmd 'awe status' -Until 'features=\d+' -Expect "role=$($pair[1]) inWorld=True")
        if ($why) { return "$($pair[0]) should be the $($pair[1]): $why" }
    }
}))

$rows.Add((New-Row 'sync-host-sets-client-locked' -Run {
    param($Slot)
    Reset-Both
    $before = Get-SyncCounts; if ($before -is [string]) { return $before }
    foreach ($c in $cases) {
        $why = Test-Step -Slot $HostSlot -Step (New-Step -Cmd "awe set $($c.Key) $($c.Set)" -Expect ([regex]::Escape($c.Key) + " = $($c.Shown)\s+\(default $($c.Default)\)"))
        if ($why) { return "host set: $why" }
    }
    foreach ($c in $cases) {
        $shown = $c.Shown
        $why = Wait-ClientLine -Key $c.Key -What "the client to show host=$shown for $($c.Key)" -Test ({ param($l) $l -match "host=$shown\b" -and $l -match 'read-only' }.GetNewClosure())
        if ($why) { return $why }
    }
    $after = Get-SyncCounts; if ($after -is [string]) { return $after }
    if ($after.Writes -le $before.Writes) { return "the host did not publish (writes $($before.Writes) before, $($after.Writes) now)" }
    if ($after.Applied -le $before.Applied) { return "the client did not apply the host's options (applied $($before.Applied) before, $($after.Applied) now)" }
    $null
}))

$rows.Add((New-Row 'sync-client-cannot-set' -Run {
    param($Slot)
    $c = $cases[0]
    $why = Test-Step -Slot $ClientSlot -Step (New-Step -Cmd "awe set $($c.Key) 3" -Expect 'The host controls this setting')
    if ($why) { return $why }
    $why = Test-Step -Slot $ClientSlot -Step (New-Step -Cmd "awe reset $($c.Key)" -Expect "the host controls $([regex]::Escape($c.Key))")
    if ($why) { return $why }
    Wait-ClientLine -Key $c.Key -What "the client to keep host=$($c.Shown)" -Test ({ param($l) $l -match "host=$($c.Shown)\b" }.GetNewClosure())
}))

$rows.Add((New-Row 'sync-readback-and-hash' -Run {
    param($Slot)
    $h = Get-Sync $HostSlot;   if ($h -is [string]) { return $h }
    $cl = Get-Sync $ClientSlot; if ($cl -is [string]) { return $cl }
    if ($h.Role -ne 'host') { return "host reports role=$($h.Role): $($h.Head)" }
    if ($cl.Role -ne 'client') { return "client reports role=$($cl.Role): $($cl.Head)" }
    if ($h.InLobby -ne 'True' -or $cl.InLobby -ne 'True') { return "inLobby host=$($h.InLobby) client=$($cl.InLobby)" }
    if ($h.ReadbackFail -ne 0) { return "host readback_fail=$($h.ReadbackFail): $($h.Head)" }
    if ($cl.ReadbackFail -ne 0) { return "client readback_fail=$($cl.ReadbackFail): $($cl.Head)" }
    if ($h.Hash -ne $cl.Hash) { return "hash differs: host $($h.Hash) client $($cl.Hash)" }
    foreach ($c in $cases) {
        # The decoded line carries <key>=<value> pairs; only the keys are compared because the value text may be raw or described.
        if ($cl.Decoded -notmatch ([regex]::Escape($c.Key) + '=')) { return "client decoded line lacks $($c.Key): $($cl.Decoded)" }
        if ($h.Decoded -notmatch ([regex]::Escape($c.Key) + '=')) { return "host decoded line lacks $($c.Key): $($h.Decoded)" }
    }
    $null
}))

$rows.Add((New-Row 'sync-host-reset-client-follows' -Run {
    param($Slot)
    $clearedBefore = Count-CoopLines $ClientSlot "cleared the host's values"
    $before = Get-SyncCounts; if ($before -is [string]) { return $before }
    $r = Invoke-Awe -Slot $HostSlot -Cmd 'awe reset all' -Until 'reset all'
    if (-not ($r.Texts | Where-Object { $_ -match 'awe reset all: \d+ option' })) { return "host reset did not run: $(($r.Raw | Select-Object -Last 3) -join ' | ')" }
    foreach ($c in $cases) {
        $shown = $c.Shown
        $why = Wait-ClientLine -Key $c.Key -What "the client to drop host=$shown for $($c.Key)" -Test ({ param($l) $l -notmatch "host=$shown\b" }.GetNewClosure())
        if ($why) { return "$why; $(Get-HostState)" }
    }
    $cleared = Count-CoopLines $ClientSlot "cleared the host's values"
    $after = Get-SyncCounts; if ($after -is [string]) { return $after }
    # Either the client cleared the host's values or applied a new (smaller) set; one of the two must have happened.
    if ($cleared -le $clearedBefore -and $after.Applied -le $before.Applied) { return "client followed the reset but neither cleared the host's values nor applied a new set (applied $($before.Applied) before, $($after.Applied) now)" }
    $h = Get-Sync $HostSlot;    if ($h -is [string]) { return $h }
    $cl = Get-Sync $ClientSlot; if ($cl -is [string]) { return $cl }
    if ($h.ReadbackFail -ne 0 -or $cl.ReadbackFail -ne 0) { return "readback_fail host=$($h.ReadbackFail) client=$($cl.ReadbackFail) after the reset" }
    if ($h.Hash -ne $cl.Hash) { return "hash differs after the reset: host $($h.Hash) client $($cl.Hash)" }
    $null
}))

$rows.Add((New-Row 'sync-no-failures-in-log' -Run {
    param($Slot)
    foreach ($s in $HostSlot, $ClientSlot) {
        $bad = (Get-LogLines -Slot $s -Grep '[Failed]').Lines
        if ($bad.Count -gt 0) { return "$s log has [Failed] lines: $($bad[-1])" }
        $bad = (Get-LogLines -Slot $s -Grep '[CoopSync]' -Count 2000).Lines | Where-Object { $_ -match 'readback|failed|error' -and $_ -notmatch 'readback_fail=0' }
        if ($bad) { return "$s [CoopSync] reported a problem: $($bad | Select-Object -Last 1)" }
    }
    $null
}))

# A balance option (fix 7, packager route limit) set on the host reaches the client: the host's writes and the client's applied counter grow, the client's
# read-only host= value, the decoded key on both sides and equal hashes. The client's balance feature must not apply it itself.
$rows.Add((New-Row 'sync-feature-balance' -Finally 'awe reset packager.maxroutes' -Run {
    param($Slot)
    $key = 'packager.maxroutes'
    Reset-Both
    $before = Get-SyncCounts; if ($before -is [string]) { return $before }
    $why = Test-Step -Slot $HostSlot -Step (New-Step -Cmd "awe set $key 7" -Expect ([regex]::Escape($key) + ' = 7\s+\(default 5\)') -Forbid 'Exception')
    if ($why) { return "host set: $why" }
    $why = Wait-ClientLine -Key $key -What "the client to show host=7 for $key" -Test ({ param($l) $l -match 'host=7\b' -and $l -match 'read-only' })
    if ($why) { return "$why; $(Get-HostState)" }
    $after = Get-SyncCounts; if ($after -is [string]) { return $after }
    if ($after.Writes -le $before.Writes) { return "the host did not publish after setting $key (writes $($before.Writes) before, $($after.Writes) now)" }
    if ($after.Applied -le $before.Applied) { return "the client did not apply the host's options after setting $key (applied $($before.Applied) before, $($after.Applied) now)" }
    $h = Get-Sync $HostSlot;    if ($h -is [string]) { return $h }
    $cl = Get-Sync $ClientSlot; if ($cl -is [string]) { return $cl }
    if ($h.Decoded -notmatch ([regex]::Escape($key) + '=7\b')) { return "host decoded line lacks $key=7: $($h.Decoded)" }
    if ($cl.Decoded -notmatch ([regex]::Escape($key) + '=7\b')) { return "client decoded line lacks $key=7: $($cl.Decoded)" }
    if ($h.Hash -ne $cl.Hash) { return "hash differs: host $($h.Hash) client $($cl.Hash)" }
    if ($h.ReadbackFail -ne 0 -or $cl.ReadbackFail -ne 0) { return "readback_fail host=$($h.ReadbackFail) client=$($cl.ReadbackFail)" }
    # Balance is applied by the host only: the client writes no route limit.
    $why = Test-Step -Slot $ClientSlot -Step (New-Step -Cmd 'awe status' -Until '\[Dev\]   fix7-packager-routes: ' -Expect '\[Dev\]   fix7-packager-routes: syncs=\d+ packagers=\d+ writes=0\b' -Forbid 'Exception')
    if ($why) { return "client fix7 status: $why" }
    $note = (Get-LogLines -Slot $ClientSlot -Grep '[fix7-packager-routes] not the server').Lines
    if (-not ($note | Where-Object { $_ -match 'not the server, the host applies balance' })) { return "client log has no '[fix7-packager-routes] not the server, the host applies balance.' line" }
    $null
}))

$patterns = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$selected = @($rows | Where-Object {
    $name = $_.Name
    ($patterns.Count -eq 0) -or [bool]($patterns | Where-Object { $name -like $_ })
})
if ($selected.Count -eq 0) { Write-Host "FAIL setup: -Only '$($Only -join ',')' matches no check"; exit 1 }

foreach ($s in $HostSlot, $ClientSlot) {
    $notReady = Wait-GameReady -Slot $s -TimeoutSec 60
    if ($notReady) { Write-Result "game-ready-$s" $notReady; Write-Host "SUMMARY host=$HostSlot client=$ClientSlot pass=0 fail=1 skip=$($selected.Count) (no game, nothing ran)"; exit 1 }
}

foreach ($row in $selected) {
    if ($row.Pending) { Write-Result $row.Name 'placeholder, no steps yet' -Skip; continue }
    Write-Result $row.Name (Test-Row -Slot $HostSlot -Row $row)
}
# The host's options go back to default whatever the checks did.
Reset-Both
Write-Host "SUMMARY host=$HostSlot client=$ClientSlot pass=$($script:Passed) fail=$($script:Failed) skip=$($script:Skipped)"
exit $script:Failed
