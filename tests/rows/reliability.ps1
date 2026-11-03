# Rows of the reliability fixes that go beyond the dev-command smoke rows in awe-check.ps1.
# Dot-sourced by awe-check.ps1: $rows, New-Row, New-Step, Step-Feature and Invoke-Awe are in scope.

# Fix 1 counts warps it refused because the destination has no NavMesh within 5 m.
$rows.Add((New-Row 'rel-fix1-refused-counter' -Steps @(
    Step-Feature 'fix1-standstill' 'on=11 checks=\d+ warps=\d+ gaveup=\d+ partial=\d+ partialwarps=\d+ refused=\d+')))

# Fix 3 reports deferred releases (a disabled task while another task of the employee runs).
$rows.Add((New-Row 'rel-fix3-deferred-counter' -Steps @(
    Step-Feature 'fix3-lock-leak' 'on=11 sweeps=\d+ stationLocks=\d+ slotLocks=\d+ sleepReleases=\d+ deferred=\d+ pending=\d+')))

# Fix 3 must not free the locks of the running task when an idle behaviour of the same employee is disabled. The probe needs
# an employee that works a task and holds a station or slot lock (for example a chemist at a cauldron or a packager that has
# reserved a destination); the row waits up to 90 s for one and fails when none shows up.
$rows.Add((New-Row 'rel-fix3-active-lock-kept' -Run {
    param($Slot)
    $deadline = (Get-Date).AddSeconds(90)
    $line = $null
    while ($true) {
        $r = Invoke-Awe -Slot $Slot -Cmd 'awe lockleak probe' -Until '\[Fix3\] probe'
        if ($r.ModErrors.Count -gt 0) { return "'awe lockleak probe' logged an error: $($r.ModErrors -join ' | ')" }
        $line = $r.Texts | Where-Object { $_ -match '\[Fix3\] probe .+: active=\S+ disabled=\S+ locksBefore=' } | Select-Object -Last 1
        if ($line) { break }
        if ((Get-Date) -gt $deadline) {
            return 'precondition not met: no employee held a lock while working a task within 90 s; start a chemist or packager task and run again'
        }
        Start-Sleep -Seconds 5
    }
    if ($line -match 'LOST') { return "the active task lost its lock: $line" }
    if ($line -notmatch 'HELD') { return "probe line has no verdict: $line" }
    $null
}))

# Fix 5 explains lab ovens: the summary line appears and every lab oven reason is one of the known texts.
$rows.Add((New-Row 'rel-fix5-reason-lines' -Steps @(
    New-Step -Cmd 'awe reasons' -Until '\[Fix5\] \d+ employee\(s\)\.' -Expect '\[Fix5\] \d+ employee\(s\)\. on=1 rewritten=\d+ kept=\d+' -Forbid 'Exception' -Check {
        param($r, $texts)
        foreach ($t in $texts) {
            if ($t -notmatch '^\[Dev\] \[Fix5\] .+: (?<why>.+)$') { continue }
            $why = $Matches.why   # the next -match replaces $Matches
            if ($why -match '^A lab oven' -and
                $why -notmatch '^A lab oven(\x27s output is full| is in use| is still cooking| is missing ingredients)\.') {
                return "unknown lab oven reason: $t"
            }
        }
    })))
