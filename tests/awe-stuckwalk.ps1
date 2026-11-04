<#
An employee whose walk never advances gets going again.

    pwsh -File tests/awe-stuckwalk.ps1 -Slot mptest3 [-Build <worktree folder or catalog name>] [-Profile stack|main] [-NoStart]

The signature from a tester report: a chemist stands still on a work task until the player talks to it. The agent still
holds its path, so NPCMovement.IsMoving stays true and the work behaviour waits for the walk forever; the conversation
ends the walk through Movement.Stop(). `awe standstill freeze` builds that state on purpose (NavMeshAgent.isStopped on a
walking employee: the path stays, the employee does not move).

Rows:
  off-froze      with Fix 1 off, an employee walking for a work task was frozen
  off-stuck      with Fix 1 off, that employee is still on the same walk at the same distance after the wait (the reproduction)
  on-froze       with Fix 1 on, an employee walking for a work task was frozen
  on-stop-logged the log has "[Fix1] stopped the walk of <name>" and the stops counter rose
  on-recovered   that employee left the frozen spot (distance to its destination changed, or the walk ended)
-NoStart uses the game already running in the slot. Prints PASS or FAIL per row and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$Build = '',
    [ValidateSet('stack', 'main')][string]$Profile = 'stack',
    [switch]$NoStart,
    [int]$WaitSec = 30
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'awe-lib.ps1')
$root = $script:Root
if (-not $Build) { $Build = Split-Path $script:RepoDir -Leaf }
$session = Join-Path $root 'Workspace\tools\session\session.ps1'
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function Awe([string]$cmd, [string]$until = '') { Invoke-Awe -Slot $Slot -Cmd $cmd -Until $until }

# Freezes the first employee that walks for a work task; retries while nobody walks. Returns @{Name; Dist} or $null.
function Freeze([int]$timeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $r = Awe 'awe standstill freeze' 'froze |no employee is walking'
        $line = @($r.Texts | Where-Object { $_ -match '\[Fix1\] froze (?<n>.+?) \((?<b>[^)]+)\): .* (?<d>[\d.]+) m from its destination' }) | Select-Object -First 1
        if ($line -and $line -match '\[Fix1\] froze (?<n>.+?) \((?<b>[^)]+)\): .* (?<d>[\d.]+) m from its destination') {
            Write-Host "  $line"
            return @{ Name = $Matches.n; Behaviour = $Matches.b; Dist = [double]$Matches.d }
        }
        Start-Sleep -Seconds 3
    }
    return $null
}

# The dev line of one employee: @{Moving; Dist; Behaviour} or $null.
function State([string]$name) {
    $r = Awe 'awe standstill' 'on=\d+ checks='
    $rx = '\[Fix1\] ' + [regex]::Escape($name) + ': behaviour=(?<b>\S+) work=\S+ moving=(?<m>\S+) distToDest=(?<d>[\d.]+)'
    $line = @($r.Texts | Where-Object { $_ -match $rx }) | Select-Object -First 1
    $stops = @($r.Texts | Where-Object { $_ -match ' stops=(\d+)' }) | Select-Object -First 1
    $s = if ($stops -and $stops -match ' stops=(\d+)') { [int]$Matches[1] } else { -1 }
    if (-not ($line -and $line -match $rx)) { return @{ Found = $false; Stops = $s } }
    @{ Found = $true; Moving = $Matches.m; Dist = [double]$Matches.d; Behaviour = $Matches.b; Stops = $s }
}

try {
    if (-not $NoStart) {
        & pwsh -NoProfile -File $session -Slot $Slot -Build $Build -Profile $Profile -NoApp
        if ($LASTEXITCODE -ne 0) { Result $false 'session-start' "session.ps1 exit $LASTEXITCODE"; return }
    }
    $null = Awe 'awe set diagnostics.logfixactions true' 'logfixactions = '
    $null = Awe 'awe set reliability.standstillseconds 5' 'standstillseconds = '

    # ---- Fix 1 off: the frozen walk stays frozen -------------------------------------------------------------
    $null = Awe 'awe set reliability.fixstandstill false' 'fixstandstill = '
    $f = Freeze
    Result ($null -ne $f) 'off-froze' $(if ($f) { "$($f.Name) ($($f.Behaviour)) $($f.Dist) m from its destination" } else { 'no employee walked for a work task within 120 s' })
    if ($f) {
        Start-Sleep -Seconds $WaitSec
        $st = State $f.Name
        $stuck = $st.Found -and $st.Moving -eq 'True' -and [math]::Abs($st.Dist - $f.Dist) -lt 0.5
        Result $stuck 'off-stuck' $(if ($st.Found) { "after $WaitSec s: moving=$($st.Moving) distToDest=$($st.Dist) (was $($f.Dist)) behaviour=$($st.Behaviour)" } else { "$($f.Name) not in the dev list" })
    }
    $null = Awe 'awe standstill thaw' 'thawed'

    # ---- Fix 1 on: the frozen walk is stopped and the employee goes on ---------------------------------------
    $null = Awe 'awe set reliability.fixstandstill true' 'fixstandstill = '
    $before = (State '-').Stops
    $f = Freeze
    Result ($null -ne $f) 'on-froze' $(if ($f) { "$($f.Name) ($($f.Behaviour)) $($f.Dist) m from its destination" } else { 'no employee walked for a work task within 120 s' })
    if ($f) {
        $rx = '\[Fix1\] stopped the walk of ' + [regex]::Escape($f.Name)
        $err = Wait-Until -TimeoutSec $WaitSec -What 'the Fix 1 stop line' -Condition {
            @((Get-LogLines -Slot $Slot -Grep '[Fix1] stopped the walk of').Lines | Where-Object { $_ -match $rx }).Count -ge 1 }
        $st = State $f.Name
        Result ($null -eq $err -and $st.Stops -gt $before) 'on-stop-logged' $(if ($err) { $err } else { "stops $before -> $($st.Stops)" })
        Start-Sleep -Seconds 5
        $st = State $f.Name
        $moved = $st.Found -and ([math]::Abs($st.Dist - $f.Dist) -ge 0.5 -or $st.Moving -ne 'True')
        Result $moved 'on-recovered' $(if ($st.Found) { "moving=$($st.Moving) distToDest=$($st.Dist) (was $($f.Dist)) behaviour=$($st.Behaviour)" } else { "$($f.Name) not in the dev list" })
    }
}
finally {
    try {
        $null = Awe 'awe standstill thaw' 'thawed'
        foreach ($k in 'reliability.fixstandstill', 'reliability.standstillseconds', 'diagnostics.logfixactions') { $null = Awe "awe reset $k" '' }
    }
    catch { Write-Host "  cleanup failed: $($_.Exception.Message)" }
    Write-Host "SUMMARY awe-stuckwalk build=$Build pass=$pass fail=$fail"
}
