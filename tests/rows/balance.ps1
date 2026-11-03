# Rows of the balance features: packager speed field, wage and fee reset on the prefab, cleaner action times, sprinklers.
# Dot-sourced by awe-check.ps1; uses its helpers (New-Row, New-Step, Step-Feature, Test-Eff, Test-Step, $syncedCheck).

# Polls 'awe eff <key>' every 0.5 s, at most $TimeoutSec, until $Pred returns true for the Eff result. The balance
# features re-apply once a second. Returns the last Eff result ($null Reason on success of the reading itself).
function Wait-Eff {
    param([string]$Slot, [string]$Key, [scriptblock]$Pred, [int]$TimeoutSec = 12)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($true) {
        $e = Test-Eff $Slot $Key
        if ($e.Reason -or (& $Pred $e) -or [DateTime]::UtcNow -ge $deadline) { return $e }
        Start-Sleep -Milliseconds 500
    }
}

# Sets an option and checks the console echo.
function Set-AweOption {
    param([string]$Slot, [string]$Key, [string]$Raw)
    Test-Step -Slot $Slot -Step (New-Step -Cmd "awe set $Key $Raw" -Expect ('\[Dev\]\s+' + [regex]::Escape($Key) + ' = ') -Forbid 'Exception')
}

# -- packager speed: the live value is the multiplier the game's packaging coroutines divide by --
$rows.Add((New-Row 'bal-packager-speed' -Finally 'awe reset packager.packagingspeed' -Run {
    param($Slot)
    $key = 'packager.packagingspeed'
    $e = Test-Eff $Slot $key
    if ($e.Reason) { return $e.Reason }
    if ($e.Live -notmatch '^multiplier (?<m>[\d.]+)$') { return "no packager is hired (live '$($e.Live)'); hire one and run again" }
    $base = [double]::Parse($Matches.m, [cultureinfo]::InvariantCulture)
    $why = Set-AweOption $Slot $key '2'
    if ($why) { return $why }
    $e = Wait-Eff $Slot $key { param($r) $r.Live -match '^multiplier (?<m>[\d.]+)$' -and [math]::Abs([double]::Parse($Matches.m, [cultureinfo]::InvariantCulture) - 2 * $base) -lt 0.01 }
    if ($e.Reason) { return $e.Reason }
    if ($e.Live -notmatch '^multiplier (?<m>[\d.]+)$' -or [math]::Abs([double]::Parse($Matches.m, [cultureinfo]::InvariantCulture) - 2 * $base) -ge 0.01) {
        return "after 'awe set $key 2' the packager's PackagingSpeedMultiplier is '$($e.Live)', expected multiplier $(2 * $base)"
    }
    Write-Host "  NOTE bal-packager-speed: PackagingSpeedMultiplier $base -> $($e.Live)"
    $why = Test-Step -Slot $Slot -Step (New-Step -Cmd "awe reset $key" -Expect ('\[Dev\]\s+' + [regex]::Escape($key) + ' = ') -Forbid 'Exception')
    if ($why) { return $why }
    $e = Wait-Eff $Slot $key { param($r) $r.Live -match '^multiplier (?<m>[\d.]+)$' -and [math]::Abs([double]::Parse($Matches.m, [cultureinfo]::InvariantCulture) - $base) -lt 0.01 }
    if ($e.Reason) { return $e.Reason }
    if ($e.Live -notmatch '^multiplier (?<m>[\d.]+)$' -or [math]::Abs([double]::Parse($Matches.m, [cultureinfo]::InvariantCulture) - $base) -ge 0.01) {
        return "after 'awe reset $key' the multiplier is '$($e.Live)', expected the game's $base"
    }
    $null
}))

# Station and press times drive the same field while the packager works at the station; without a working packager the
# live reading stays 'not seen yet', which is noted, not failed.
$rows.Add((New-Row 'bal-packager-station-times' -Finally 'awe reset packager.packagingstationtime', 'awe reset packager.brickpresstime' -Run {
    param($Slot)
    foreach ($pair in @(@('packager.packagingstationtime', '10', 5.0), @('packager.brickpresstime', '30', 15.0))) {
        $key = $pair[0]
        $why = Set-AweOption $Slot $key $pair[1]
        if ($why) { return $why }
        $e = Wait-Eff $Slot $key { param($r) $r.Configured -eq $pair[1] } 4
        if ($e.Reason) { return $e.Reason }
        if ($e.Configured -ne $pair[1]) { return "$key configured=$($e.Configured) after setting $($pair[1])" }
        if ($e.Live -eq 'not seen yet') { Write-Host "  NOTE bal-packager-station-times: $key live=not seen yet (no packager worked at it)" }
        elseif ($e.Live -notmatch '^[\d.]+ s at work speed') { return "$key live reading '$($e.Live)' does not parse" }
        else { Write-Host "  NOTE bal-packager-station-times: $key live=$($e.Live)" }
    }
    $null
}))

# -- wage and signing fee: the prefab is what a new hire copies. Setting and resetting must end at the game's value on the
# prefab and on the first hire. There is no console command that hires, so the prefab reading stands in for a new hire. --
$rows.Add((New-Row 'bal-money-reset-prefab' -Finally 'awe reset botanist.dailywage', 'awe reset botanist.signingfee', 'awe reset chemist.dailywage', 'awe reset packager.dailywage', 'awe reset cleaner.dailywage' -Run {
    param($Slot)
    $pairs = @(@('botanist.dailywage', '770'), @('chemist.dailywage', '770'), @('packager.dailywage', '770'), @('cleaner.dailywage', '770'), @('botanist.signingfee', '1550'))
    $parse = { param($live) if ($live -match '^employee (?<e>\S+) prefab (?<p>\S+)$') { @{ Employee = $Matches.e; Prefab = $Matches.p } } else { $null } }
    foreach ($pair in $pairs) {
        $key = $pair[0]; $raw = $pair[1]
        $e = Test-Eff $Slot $key
        if ($e.Reason) { return $e.Reason }
        $v = & $parse $e.Live
        if (-not $v -or $v.Prefab -eq 'none') { return "$key has no prefab reading (live '$($e.Live)')" }
        $van = $v.Prefab
        $why = Set-AweOption $Slot $key $raw
        if ($why) { return $why }
        $e = Wait-Eff $Slot $key { param($r) $v = & $parse $r.Live; $v -and $v.Prefab -eq $raw -and ($v.Employee -eq 'none' -or $v.Employee -eq $raw) }
        if ($e.Reason) { return $e.Reason }
        $v = & $parse $e.Live
        if (-not $v -or $v.Prefab -ne $raw -or ($v.Employee -ne 'none' -and $v.Employee -ne $raw)) { return "$key set to $raw but live is '$($e.Live)'" }
        $why = Test-Step -Slot $Slot -Step (New-Step -Cmd "awe reset $key" -Expect ('\[Dev\]\s+' + [regex]::Escape($key) + ' = ') -Forbid 'Exception')
        if ($why) { return $why }
        $e = Wait-Eff $Slot $key { param($r) $v = & $parse $r.Live; $v -and $v.Prefab -eq $van -and ($v.Employee -eq 'none' -or $v.Employee -eq $van) }
        if ($e.Reason) { return $e.Reason }
        $v = & $parse $e.Live
        if (-not $v -or $v.Prefab -ne $van -or ($v.Employee -ne 'none' -and $v.Employee -ne $van)) { return "$key after reset is '$($e.Live)', expected the game's $van on prefab and hire" }
        Write-Host "  NOTE bal-money-reset-prefab: $key vanilla $van, set $raw, reset back to $van"
    }
    $null
}))

# -- cleaner action times: each option scales the waits of its action on the host --
# Live reads "last wait <before> s -> <after> s" once a cleaner performed the action with the option changed. The ratio
# after/before must equal the option's scale; 'not seen yet' only says that no cleaner did the action meanwhile.
$rows.Add((New-Row 'bal-cleaner-times' -Finally 'awe reset cleaner.workspeed', 'awe reset cleaner.trashpickuptime', 'awe reset cleaner.bagtime', 'awe reset cleaner.disposetime', 'awe reset cleaner.emptygrabbertime' -Run {
    param($Slot)
    $why = Test-Step -Slot $Slot -Step (Step-Feature 'bal-cleaner' 'syncs=\d+ cleaners=\d+' $syncedCheck)
    if ($why) { return $why }
    # key, raw value, expected after/before for the waits that option touches
    $cases = @(
        @('cleaner.workspeed', '2', 0.5),
        @('cleaner.trashpickuptime', '0.45', 0.5),
        @('cleaner.bagtime', '1.5', 0.5),
        @('cleaner.disposetime', '0.6', 0.5),
        @('cleaner.emptygrabbertime', '2', 2.0))
    foreach ($c in $cases) {
        $key = $c[0]
        $why = Set-AweOption $Slot $key $c[1]
        if ($why) { return $why }
        $e = Wait-Eff $Slot $key { param($r) $r.Configured -ne '' } 4
        if ($e.Reason) { return $e.Reason }
        if ([math]::Abs([double]::Parse($e.Configured, [cultureinfo]::InvariantCulture) - [double]::Parse($c[1], [cultureinfo]::InvariantCulture)) -gt 0.0005) { return "$key configured=$($e.Configured) after setting $($c[1])" }
        if ($e.Live -match '^last wait (?<b>[\d.]+) s -> (?<a>[\d.]+) s$') {
            $ratio = [double]::Parse($Matches.a, [cultureinfo]::InvariantCulture) / [double]::Parse($Matches.b, [cultureinfo]::InvariantCulture)
            if ([math]::Abs($ratio - $c[2]) -gt 0.03 * $c[2]) { return "${key}: the cleaner wait went $($e.Live), expected a factor of $($c[2])" }
            Write-Host "  NOTE bal-cleaner-times: $key $($e.Live)"
        }
        elseif ($e.Live -eq 'not seen yet') { Write-Host "  NOTE bal-cleaner-times: $key configured, live=not seen yet (no cleaner did the action)" }
        else { return "$key live reading '$($e.Live)' does not parse" }
        $why = Test-Step -Slot $Slot -Step (New-Step -Cmd "awe reset $key" -Expect ('\[Dev\]\s+' + [regex]::Escape($key) + ' = ') -Forbid 'Exception')
        if ($why) { return $why }
    }
    $null
}))

# -- sprinklers: the coverage set is rebuilt every 5 s while the option is on --
$rows.Add((New-Row 'bal-sprinklers' -Finally 'awe reset botanist.usesprinklers' -Run {
    param($Slot)
    $key = 'botanist.usesprinklers'
    $why = Test-Step -Slot $Slot -Step (Step-Feature 'bal-sprinklers' 'syncs=\d+ sprinklers=\d+ pots=\d+' $syncedCheck)
    if ($why) { return $why }
    $why = Set-AweOption $Slot $key 'true'
    if ($why) { return $why }
    $live = '^(?<s>\d+) sprinkler\(s\) reach (?<p>\d+) pot\(s\), (?<a>\d+) left to them now$'
    $e = Wait-Eff $Slot $key { param($r) $r.Configured -eq 'true' -and $r.Live -match $live } 15
    if ($e.Reason) { return $e.Reason }
    if ($e.Configured -ne 'true') { return "$key configured=$($e.Configured) after setting true" }
    if ($e.Live -notmatch $live) { return "live reading '$($e.Live)' does not parse" }
    # A sprinkler nobody used holds no pot back.
    if ([int]$Matches.a -ne 0) { return "pots are left to sprinklers before any spray: $($e.Live)" }
    if ([int]$Matches.p -eq 0) { Write-Host "  NOTE bal-sprinklers: $($Matches.s) sprinkler(s), none reaches a pot; the spray step is skipped" }
    else {
        $pots = [int]$Matches.p
        $why = Test-Step -Slot $Slot -Step (New-Step -Cmd 'awe sprinkle' -Until '\[Sprinklers\]' -Expect '\[Dev\] \[Sprinklers\] .+ sprays \d+ pot\(s\)\.' -Forbid 'Exception')
        if ($why) { return $why }
        $e = Wait-Eff $Slot $key { param($r) $r.Live -match $live -and [int]$Matches.a -gt 0 } 10
        if ($e.Reason) { return $e.Reason }
        if ($e.Live -notmatch $live -or [int]$Matches.a -eq 0) { return "no pot is left to the sprinkler 10 s after a spray: $($e.Live)" }
        if ([int]$Matches.a -gt $pots) { return "more pots left to sprinklers than they reach: $($e.Live)" }
        Write-Host "  NOTE bal-sprinklers: after a spray $($e.Live)"
    }
    $why = Test-Step -Slot $Slot -Step (New-Step -Cmd "awe reset $key" -Expect ('\[Dev\]\s+' + [regex]::Escape($key) + ' = ') -Forbid 'Exception')
    if ($why) { return $why }
    $e = Wait-Eff $Slot $key { param($r) $r.Live -match '^0 sprinkler' } 8
    if ($e.Reason) { return $e.Reason }
    if ($e.Live -notmatch '^0 sprinkler\(s\) reach 0 pot\(s\), 0 left to them now$') { return "after reset the coverage is '$($e.Live)', expected none" }
    $null
}))
