# Rows of the diagnostics features: Setup Doctor, idle analytics, the app's diagnostics state. Dot-sourced by awe-check.ps1,
# so $rows, $groupCounts, $totalOptions and the Step-* helpers exist here.

$doctorOpt = 'diagnostics.enablesetupdoctor'
$maxOpt    = 'diagnostics.doctormaxfindings'
$anOpt     = 'diagnostics.enableanalytics'
$anIntOpt  = 'diagnostics.analyticsintervalseconds'
$badgeOpt  = 'diagnostics.showproblembadges'

# "[Dev] [Doctor] <n> finding(s), <shown> shown, ..." : shown never exceeds the finding count or the configured limit.
$doctorSummary = '\d+ finding\(s\), \d+ shown, \d+ propert\(ies\), \d+ employee\(s\), \d+ route\(s\), \d+ path-tested, \d+ ms'
$shownWithin = {
    param($limit)
    {
        param($r, $texts)
        $l = $texts | Where-Object { $_ -match '\[Dev\] \[Doctor\] (\d+) finding\(s\), (\d+) shown' } | Select-Object -Last 1
        if (-not $l) { return 'no summary line' }
        [void]($l -match '\[Dev\] \[Doctor\] (?<t>\d+) finding\(s\), (?<s>\d+) shown')
        if ([int]$Matches.s -gt [int]$Matches.t) { return "shows $($Matches.s) of only $($Matches.t) finding(s)" }
        if ([int]$Matches.s -gt $limit) { return "shows $($Matches.s) finding(s), the limit is $limit" }
    }.GetNewClosure()
}

$rows.Add((New-Row 'diag-doctor-off' -Steps @(
    New-Step -Cmd "awe set $doctorOpt false" -Expect '\[Dev\]\s+diagnostics\.enablesetupdoctor = '
    New-Step -Cmd 'awe doctor' -Until '\[Dev\] \[Doctor\] ' -Expect '\[Dev\] \[Doctor\] The Setup Doctor is off\.' -Forbid 'Exception')))

$rows.Add((New-Row 'diag-doctor' -Finally @("awe reset $doctorOpt", "awe reset $maxOpt", 'awe reset diagnostics.doctorcheckreachability') -Steps @(
    New-Step -Cmd "awe set $doctorOpt true" -Expect '\[Dev\]\s+diagnostics\.enablesetupdoctor = On'
    Step-Feature 'setup-doctor' 'on=1 runs=\d+ findings=(-|\d+)'
    Step-Dev 'awe doctor' 'Doctor' $doctorSummary -Check (& $shownWithin 25)
    New-Step -Cmd 'awe set diagnostics.doctorcheckreachability true' -Expect '\[Dev\]\s+diagnostics\.doctorcheckreachability = On'
    Step-Dev 'awe doctor' 'Doctor' $doctorSummary -Check (& $shownWithin 25)
    New-Step -Cmd "awe set $maxOpt 5" -Expect '\[Dev\]\s+diagnostics\.doctormaxfindings = 5'
    Step-Dev 'awe doctor' 'Doctor' $doctorSummary -Check (& $shownWithin 5)
    Step-Feature 'setup-doctor' 'on=1 runs=[1-9]\d* findings=\d+')))

$anStat = 'on=1 interval=1 tracked=\d+ samples=\d+ idle=\d+'
$rows.Add((New-Row 'diag-analytics' -Finally @("awe reset $anOpt", "awe reset $anIntOpt") -Steps @(
    New-Step -Cmd "awe set $anIntOpt 1" -Expect '\[Dev\]\s+diagnostics\.analyticsintervalseconds = 1'
    New-Step -Cmd "awe set $anOpt true" -Expect '\[Dev\]\s+diagnostics\.enableanalytics = On'
    Step-Feature 'idle-analytics' $anStat
    Step-Dev 'awe analytics' 'Analytics' $anStat -Employee '.+: idle \d+% of \d+ sample\(s\), \d+ reason\(s\)'
    Step-Dev 'awe analytics reset' 'Analytics' 'on=1 interval=1 tracked=0 samples=0 idle=0' -Also '\[Dev\] \[Analytics\] cleared\.')))

# The app state: all options still rendered, and the diagnostics call answers for every panel.
$rows.Add((New-Row 'diag-app-dump' -Steps @(
    New-Step -Cmd 'awe app dump' -Until 'awe app dump: ' -Forbid 'Exception' `
        -Expect "awe app dump: $($groupCounts.Count) groups, $totalOptions options rendered, 0 locked, role=(single-player|host), \d+ chars\.")))

$rows.Add((New-Row 'diag-app-state' -Finally @("awe reset $badgeOpt", "awe reset $doctorOpt", "awe reset $anOpt") -Steps @(
    New-Step -Cmd 'awe app diag' -Until 'awe app diag: ' -Forbid 'Exception' `
        -Expect 'awe app diag: host=1 badges=0/0 doctor=(none|ran):\d+ analytics=0/\d+ notice=0\.'
    New-Step -Cmd "awe set $badgeOpt true" -Expect '\[Dev\]\s+diagnostics\.showproblembadges = On'
    New-Step -Cmd "awe set $doctorOpt true" -Expect '\[Dev\]\s+diagnostics\.enablesetupdoctor = On'
    New-Step -Cmd "awe set $anOpt true" -Expect '\[Dev\]\s+diagnostics\.enableanalytics = On'
    New-Step -Cmd 'awe doctor' -Until '\[Dev\] \[Doctor\] ' -Expect '\[Dev\] \[Doctor\] \d+ finding'
    New-Step -Cmd 'awe app diag' -Until 'awe app diag: ' -Forbid 'Exception' `
        -Expect 'awe app diag: host=1 badges=\d+/\d+ doctor=ran:\d+ analytics=\d+/\d+ notice=0\.')))

# The page itself (selectors for s1.py eval / click; the lead drives these in the open app):
#   #group-diagnostics  #diag  #diag-refresh  #diag-badges  #diag-badges-count  #diag-badges-note  .badge-row
#   #diag-doctor  #diag-doctor-run  #diag-doctor-summary  #diag-doctor-empty  #diag-doctor-more  .finding
#   #diag-analytics  #diag-analytics-reset  #diag-analytics-status  .an-row
#   #opt-diagnostics-<id>  with .opt-on .opt-off .opt-minus .opt-plus .opt-field .opt-reset
#   #group-hint (hint above the open group, visible text under .hint, class hidden when empty)  #band  #band-text
#   #reset-all  #reset-all-confirm  #reset-all-cancel  #reset-group  #preset-fast (host and single player only)
