# Rows of the core area: native patch targets, HWE warning, verbose log, co-op sync size. Dot-sourced by awe-check.ps1.

# -- patch targets: every multi-method patch reports how many targets it kept, refused (address unreadable) and skipped
# (address already patched by another mod). A healthy game refuses nothing, because a refused target is a missing patch.
$rows.Add((New-Row 'core-native-targets' -Steps @(
    New-Step -Log '[NativeMethods]' `
        -Expect '\[NativeMethods\] \S+: \d+ patch target\(s\) for \d+ method\(s\), \d+ refused \(address unknown\), \d+ skipped \(address patched by another mod\)\.' `
        -Forbid 'not patched' `
        -Check {
            param($r, $texts)
            foreach ($t in $texts) {
                if ($t -match '(\d+) refused \(address unknown\), (\d+) skipped') {
                    if ([int]$Matches[1] -ne 0) { return "refused target(s): $t" }
                }
            }
        })))

# -- exactly one HWE message; the removed option is gone --
$rows.Add((New-Row 'core-hwe-single-warning' -Steps @(
    New-Step -Log '[Compat]' -Expect '\[Compat\] .*(is not loaded|is loaded\. Actually Working Employees replaces)' -Forbid 'switched off by its option' -Check {
        param($r, $texts)
        $loaded = @($texts | Where-Object { $_ -match 'is loaded\.' })
        if ($loaded.Count -gt 1) { return "$($loaded.Count) HWE messages, expected one" }
    }
    New-Step -Cmd 'awe get general.warnonhwe' -Expect (Rx-Warn "no option 'general.warnonhwe'"))))

# -- Verbose log: with it on a changed option writes a "[Options] <key> = <value>" line, with it off nothing --
$rows.Add((New-Row 'core-verbose-logging' -Finally @('awe reset general.verboselogging', 'awe reset botanist.maxpots') -Steps @(
    Step-Set 'general.verboselogging' 'on' 'On' 'Off'
    New-Step -Cmd 'awe set botanist.maxpots 13' -Expect (Rx-OptSet 'botanist.maxpots' '13' '8'), '\[Options\] botanist\.maxpots = 13'
    Step-Reset 'general.verboselogging' 'Off'
    New-Step -Cmd 'awe set botanist.maxpots 12' -Expect (Rx-OptSet 'botanist.maxpots' '12' '8') -Forbid '\[Options\] botanist\.maxpots')))

# -- co-op sync payload: the size of what the host would publish is in the status line and stays far below Steam's 8 KB --
$rows.Add((New-Row 'core-sync-size' -Steps @(
    Step-Feature 'coop-sync' 'role=\S+ bytes=\d+ wire=\d+ ' {
        param($r, $texts)
        $l = $texts | Where-Object { $_ -match 'coop-sync: .*wire=(\d+)' } | Select-Object -Last 1
        [void]($l -match 'wire=(\d+)')
        if ([int]$Matches[1] -gt 3000) { "sync payload is $($Matches[1]) bytes, over the 3000 byte budget" }
    })))
