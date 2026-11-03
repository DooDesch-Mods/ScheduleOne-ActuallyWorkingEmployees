# Rows for packager batch handling, route priority and diagnostics, shared idle points and the collision switches.
# Dot-sourced by tests/awe-check.ps1 (uses $rows, New-Row, New-Step, Step-Set, Step-Feature, Step-Dev, Step-Reset).

# -- packager routes: the feature line carries the live option values and the counters --
$routeStat = 'batch=\d threshold=\d+ mode=\w+ diag=\d picked=\d+ vetoed=\d+ released=\d+ timeouts=\d+ reordered=\d+ diagnosed=\d+'
$rows.Add((New-Row 'batch-feature' -Steps @(
    Step-Feature 'packager-routes' $routeStat
    Step-Dev 'awe batch' 'Batch' '\d+ packager\(s\), \d+ route\(s\)\. ' '.+')))

# The options reach the feature: the status line shows the new values at once, and the dump uses them.
$rows.Add((New-Row 'batch-options-reach-feature' -Finally 'awe reset packager.batchhandling', 'awe reset packager.batchthreshold', 'awe reset packager.routeprioritymode', 'awe reset packager.routediagnostics', 'awe reset packager.batchfullstackonly' -Steps @(
    Step-Set 'packager.batchhandling' 'true' 'On' 'Off'
    Step-Set 'packager.batchthreshold' '25' '25' '10'
    Step-Set 'packager.routeprioritymode' 'Fullest' 'Fullest' 'Vanilla'
    Step-Set 'packager.routediagnostics' 'true' 'On' 'Off'
    Step-Feature 'packager-routes' 'batch=1 threshold=25 mode=Fullest diag=1 '
    Step-Dev 'awe batch' 'Batch' '\d+ packager\(s\), \d+ route\(s\)\. batch=1 threshold=25 mode=Fullest diag=1 ' '.+' -Check {
        param($r, $texts)
        # A route with a ready load must report a decision that uses the threshold of 25.
        foreach ($t in $texts) {
            if ($t -match 'route \d+: ready=(\d+) need=(\d+) decision=(wait|go:\w+)') {
                if ([int]$Matches[2] -ne 25) { return "need=$($Matches[2]) instead of the threshold 25: $t" }
                if ([int]$Matches[1] -lt 25 -and $Matches[3] -eq 'go:Threshold') { return "released by the threshold with only $($Matches[1]) ready: $t" }
            }
        }
    }
    Step-Set 'packager.batchfullstackonly' 'true' 'On' 'Off'
    Step-Dev 'awe batch' 'Batch' '\d+ packager\(s\), \d+ route\(s\)\. ' '.+' -Check {
        param($r, $texts)
        foreach ($t in $texts) {
            if ($t -match 'route \d+: ready=\d+ need=(\d+) decision=go:Threshold') { return "full-stack-only mode released by the threshold: $t" }
            if ($t -match 'route \d+: ready=\d+ need=(\d+)' -and [int]$Matches[1] -ne 0) { return "full-stack-only mode reports need=$($Matches[1]): $t" }
        }
    })))

# Threshold 1 acts as vanilla: no route is held back by the batch.
$rows.Add((New-Row 'batch-threshold-one-is-vanilla' -Finally 'awe reset packager.batchhandling', 'awe reset packager.batchthreshold' -Steps @(
    Step-Set 'packager.batchhandling' 'true' 'On' 'Off'
    Step-Set 'packager.batchthreshold' '1' '1' '10'
    Step-Dev 'awe batch' 'Batch' '\d+ packager\(s\), \d+ route\(s\)\. ' '.+' -Check {
        param($r, $texts)
        foreach ($t in $texts) { if ($t -match 'decision=wait') { return "a route waits with threshold 1: $t" } }
    })))

# Priority modes: every mode scores each ready route; the dump prints the score.
$rows.Add((New-Row 'route-priority-modes' -Finally 'awe reset packager.routeprioritymode' -Steps @(
    Step-Set 'packager.routeprioritymode' 'Fullest' 'Fullest' 'Vanilla'
    Step-Dev 'awe batch' 'Batch' '\d+ packager\(s\), \d+ route\(s\)\. .*mode=Fullest'
    Step-Set 'packager.routeprioritymode' 'Emptiest' 'Emptiest' 'Vanilla'
    Step-Dev 'awe batch' 'Batch' '\d+ packager\(s\), \d+ route\(s\)\. .*mode=Emptiest'
    Step-Set 'packager.routeprioritymode' 'NearestFirst' 'NearestFirst' 'Vanilla'
    Step-Dev 'awe batch' 'Batch' '\d+ packager\(s\), \d+ route\(s\)\. .*mode=NearestFirst')))

$rows.Add((New-Row 'route-diagnostics-options' -Finally 'awe reset packager.routediagnostics', 'awe reset packager.routewarnintervalseconds', 'awe reset packager.logbatchactivity', 'awe reset packager.showbatchwaitingreason' -Steps @(
    Step-Set 'packager.routediagnostics' 'true' 'On' 'Off'
    Step-Set 'packager.routewarnintervalseconds' '5' '5' '30'
    Step-Set 'packager.logbatchactivity' 'false' 'Off' 'On'
    Step-Set 'packager.showbatchwaitingreason' 'false' 'Off' 'On'
    Step-Feature 'packager-routes' 'batch=\d threshold=\d+ mode=\w+ diag=1 ')))

# -- shared idle points --
$idleStat = 'on=\d role=\S+ shared=\d+ unchanged=\d+'
$rows.Add((New-Row 'idle-points-share' -Finally 'awe reset property.shareidlepoints' -Steps @(
    Step-Feature 'idle-points' $idleStat
    Step-Dev 'awe idle' 'Idle' ('\d+ property\(ies\)\. ' + $idleStat) '.+'
    Step-Set 'property.shareidlepoints' 'false' 'Off' 'On'
    Step-Feature 'idle-points' 'on=0 '
    Step-Set 'property.shareidlepoints' 'true' 'On' 'On')))

# -- collision switches: 'awe collision' runs both passes at once and prints the feature line --
$colStat = 'employees=\d furniture=\d triggerEmployees=\d+ agents=\d+ avoidanceOff=\d+ obstaclesOff=\d+ obstaclesSeen=\d+'
$rows.Add((New-Row 'collision-employees' -Finally 'awe reset general.disableemployeecollision', 'awe collision' -Steps @(
    Step-Feature 'collision' $colStat
    Step-Set 'general.disableemployeecollision' 'true' 'On' 'Off'
    Step-Dev 'awe collision' 'Collision' 'employees=1 furniture=\d triggerEmployees=\d+ agents=\d+ avoidanceOff=\d+ obstaclesOff=\d+ obstaclesSeen=\d+' -Check {
        param($r, $texts)
        # With employees in the save, the pass has changed their colliders and agents; without any, nothing can be counted.
        # Every changed agent must also have NPCMovement's own avoidance flag off, or a near player brings avoidance back.
        $l = $texts | Where-Object { $_ -match 'triggerEmployees=(\d+) agents=(\d+) avoidanceOff=(\d+)' } | Select-Object -Last 1
        [void]($l -match 'triggerEmployees=(\d+) agents=(\d+) avoidanceOff=(\d+)')
        if ([int]$Matches[1] -lt 1) { Write-Host '  NOTE collision-employees: no employee in the save, nothing was changed' }
        elseif ([int]$Matches[2] -lt 1) { return "employee colliders changed but no NavMeshAgent was found: $l" }
        elseif ([int]$Matches[3] -lt [int]$Matches[2]) { return "obstacle avoidance still on for some employees: $l" }
    }
    Step-Set 'general.disableemployeecollision' 'false' 'Off' 'Off'
    Step-Dev 'awe collision' 'Collision' 'employees=0 furniture=\d triggerEmployees=0 agents=0 avoidanceOff=0 ' -Also '\[collision\] employee collision restored on \d+ employee\(s\)')))

# A knocked-down employee must land on the floor and stand up again with employee collision off: only the root capsule
# turns into a trigger, the ragdoll bones keep colliding. Falling through the world shows as a large negative dy.
$rows.Add((New-Row 'collision-ragdoll' -Finally 'awe reset general.disableemployeecollision', 'awe collision' -Run {
    param($Slot)
    $r = Invoke-Awe -Slot $Slot -Cmd 'awe set general.disableemployeecollision true' -Until 'disableemployeecollision'
    $r = Invoke-Awe -Slot $Slot -Cmd 'awe collision' -Until '\[Collision\] employees='
    $r = Invoke-Awe -Slot $Slot -Cmd 'awe collision ragdoll' -Until '\[Collision\] ragdoll'
    $start = $r.Texts | Where-Object { $_ -match '\[Collision\] ragdoll (.+) at y=(-?[\d.]+) capsuleTrigger=(\w+)' } | Select-Object -Last 1
    if (-not $start) { return "no ragdoll line (no employee in the save?): $(($r.Texts | Select-Object -Last 2) -join ' | ')" }
    if ($Matches[3] -ne 'True') { return "the capsule is not a trigger while collision is off: $start" }
    $who = $Matches[1]
    $last = ''; $wasDown = $false
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $p = Invoke-Awe -Slot $Slot -Cmd 'awe collision probe' -Until '\[Collision\] probe'
        $last = $p.Texts | Where-Object { $_ -match '\[Collision\] probe .* ragdolled=(\w+) y=\S+ startY=\S+ dy=(-?[\d.]+) minHipDy=(\S+)' } | Select-Object -Last 1
        if (-not $last) { return "no probe line: $(($p.Texts | Select-Object -Last 2) -join ' | ')" }
        $down = $Matches[1] -eq 'True'; $dy = [double]$Matches[2]; $minHip = $Matches[3]
        # A lying body keeps its hips a few cm above the floor; 1 m below where the employee stood is through it.
        if ($minHip -ne 'none' -and [double]$minHip -lt -1) { return "$who fell through the floor: $last" }
        if ($down) { $wasDown = $true }
        elseif ($wasDown) {
            if ([math]::Abs($dy) -gt 1.5) { return "$who stood up away from the floor: $last" }
            Write-Host "  NOTE collision-ragdoll: $last"
            return $null
        }
        $t = (Get-Date).AddSeconds(1); while ((Get-Date) -lt $t) { Start-Sleep -Milliseconds 200 }
    }
    if (-not $wasDown) { return "$who never went down: $last" }
    "$who did not stand up within 30 s: $last"
}))

$rows.Add((New-Row 'collision-furniture'-Finally 'awe reset general.disablefurniturecollision', 'awe collision' -Steps @(
    Step-Set 'general.disablefurniturecollision' 'true' 'On' 'Off'
    Step-Dev 'awe collision' 'Collision' 'employees=\d furniture=1 triggerEmployees=\d+ agents=\d+ avoidanceOff=\d+ obstaclesOff=\d+ obstaclesSeen=\d+' -Check {
        param($r, $texts)
        $l = $texts | Where-Object { $_ -match 'obstaclesSeen=(\d+)' } | Select-Object -Last 1
        [void]($l -match 'obstaclesOff=(\d+) obstaclesSeen=(\d+)')
        if ([int]$Matches[2] -lt 1) { Write-Host '  NOTE collision-furniture: no NavMeshObstacle under any placed item, nothing to switch' }
        elseif ([int]$Matches[1] -gt [int]$Matches[2]) { return "more obstacles switched off than found: $l" }
    }
    Step-Set 'general.disablefurniturecollision' 'false' 'Off' 'Off'
    Step-Dev 'awe collision' 'Collision' 'employees=\d furniture=0 triggerEmployees=\d+ agents=\d+ avoidanceOff=\d+ obstaclesOff=0 ' -Also '\[collision\] furniture obstacles restored on \d+ obstacle\(s\)')))
