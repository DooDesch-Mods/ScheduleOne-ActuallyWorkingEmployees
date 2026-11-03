# Shared helpers of awe-check.ps1 and awe-coop.ps1 (dot-sourced). Everything talks to the running game through
# Workspace/tools/bridge/s1.py and tests/awe_bridge.py; nothing here starts or stops a game.

$script:TestsDir = $PSScriptRoot
$script:Root     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:RepoDir  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:S1       = Join-Path $script:Root 'Workspace\tools\bridge\s1.py'
$script:BridgePy = Join-Path $PSScriptRoot 'awe_bridge.py'

# The game answers with characters a legacy console code page cannot print.
$env:PYTHONIOENCODING = 'utf-8'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# The mod's own log lines are the ones whose source names the mod; an error from another mod in the same window is not ours.
$script:ModSource = 'Actually Working|AWE'

# ---- running things -------------------------------------------------------------------------------------

# Runs python with the arguments and returns the exit code and every output line (stdout and stderr) as strings.
function Invoke-Py {
    param([string[]]$PyArgs)
    $out = & python -I @PyArgs 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($out) }
}

# One console command through s1.py run. Waits for the log line matching -Until (default: none, the bridge settles),
# at most -MaxWaitMs. Returns the parsed log lines, the mod's error lines and whether -Until matched.
# A MAIN_THREAD_TIMEOUT means the game is still loading or hitching, so it is retried before it counts as a failure.
function Invoke-Awe {
    param([string]$Slot, [string]$Cmd, [string]$Until = '', [int]$MaxWaitMs = 10000)
    $pyArgs = @($script:S1, '--slot', $Slot, 'run', '--max-wait', "$MaxWaitMs")
    if ($Until) { $pyArgs += @('--until', $Until) }
    $pyArgs += $Cmd

    $r = $null
    for ($try = 1; $try -le 3; $try++) {
        $r = Invoke-Py $pyArgs
        if (($r.Lines -join "`n") -notmatch 'MAIN_THREAD_TIMEOUT|did not finish') { break }
        Write-Host "  (game busy, retry $try of 3: $Cmd)"
    }

    $texts = New-Object System.Collections.Generic.List[string]
    $modErrors = New-Object System.Collections.Generic.List[string]
    $untilMatched = $null
    foreach ($line in $r.Lines) {
        if ($line -match '^\[(?<lvl>[^\]]*)\] \[(?<src>[^\]]*)\] (?<txt>.*)$') {
            $texts.Add($Matches.txt)
            if ($Matches.lvl -match 'err' -and $Matches.src -match $script:ModSource) { $modErrors.Add($Matches.txt) }
        }
        elseif ($line -match '^-- .*until (?<m>NOT matched|matched)') { $untilMatched = ($Matches.m -eq 'matched') }
    }
    [pscustomobject]@{ Exit = $r.Exit; Raw = $r.Lines; Texts = @($texts); ModErrors = @($modErrors); UntilMatched = $untilMatched }
}

# Log lines of the slot's newest log that contain the substring (s1.py log --grep, a plain substring match).
function Get-LogLines {
    param([string]$Slot, [string]$Grep, [int]$Count = 60)
    $r = Invoke-Py @($script:S1, '--slot', $Slot, 'log', '--grep', $Grep, '-n', "$Count")
    [pscustomobject]@{ Exit = $r.Exit; Lines = @($r.Lines) }
}

function Get-Health {
    param([string]$Slot)
    $r = Invoke-Py @($script:BridgePy, '--slot', $Slot, 'health')
    if ($r.Exit -ne 0) { throw "health failed: $($r.Lines -join ' | ')" }
    $r.Lines[-1] | ConvertFrom-Json
}

# One bridge command that has no s1.py verb. Returns the parsed answer; throws with the reason when it fails.
function Invoke-Bridge {
    param([string]$Slot, [string]$Command, [string]$JsonArgs = '{}', [int]$TimeoutMs = 15000)
    $r = Invoke-Py @($script:BridgePy, '--slot', $Slot, '--timeout-ms', "$TimeoutMs", 'call', $Command, $JsonArgs)
    if ($r.Exit -ne 0) { throw "bridge $Command failed: $($r.Lines -join ' | ')" }
    $r.Lines[-1] | ConvertFrom-Json
}

# Runs a python command in the background and polls it in steps of at most 5 s until it exits or -TimeoutSec passes.
# Returns the exit code and output; ExitCode -1 with TimedOut when it did not finish.
function Invoke-PyBackground {
    param([string[]]$PyArgs, [int]$TimeoutSec, [string]$What)
    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $quoted = (@('-I') + $PyArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
        $p = Start-Process -FilePath 'python' -ArgumentList $quoted -NoNewWindow -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $start = Get-Date
        while (-not $p.WaitForExit(5000)) {
            Write-Host ("  ... {0}: {1:N0} s" -f $What, ((Get-Date) - $start).TotalSeconds)
            if ((Get-Date) -gt $deadline) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; Lines = @("$What did not finish in $TimeoutSec s") }
            }
        }
        $p.WaitForExit()
        $lines = @(Get-Content -LiteralPath $outFile) + @(Get-Content -LiteralPath $errFile)
        [pscustomobject]@{ ExitCode = $p.ExitCode; TimedOut = $false; Lines = $lines }
    }
    finally { Remove-Item -LiteralPath $outFile, $errFile -Force -ErrorAction SilentlyContinue }
}

# Waits until a loaded save is up (wait-ready in the background). $null when ready, else the reason.
function Wait-GameReady {
    param([string]$Slot, [int]$TimeoutSec = 300)
    $r = Invoke-PyBackground -PyArgs @($script:S1, '--slot', $Slot, 'wait-ready', '--timeout', "$TimeoutSec") `
        -TimeoutSec ($TimeoutSec + 30) -What "wait-ready $Slot"
    if ($r.ExitCode -eq 0) { return $null }
    "slot $Slot not ready: " + (($r.Lines | Select-Object -Last 2) -join ' | ')
}

# Polls a condition every 2 s up to -TimeoutSec. The scriptblock returns $true when the signal is there.
function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSec, [string]$What)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        if (& $Condition) { return $null }
        if ((Get-Date) -gt $deadline) {
            $last = if ($script:LastPollError) { " (last poll error: $($script:LastPollError))" } else { '' }
            return "timed out after $TimeoutSec s waiting for $What$last"
        }
        Start-Sleep -Seconds 2
    }
}

# ---- check rows -----------------------------------------------------------------------------------------

# A step is one console command (or one log read) plus what its output must and must not contain.
#   Cmd      console command, e.g. 'awe get botanist.maxpots'
#   Log      instead of Cmd: substring to grep for in the slot's newest log
#   Until    regex of the log line to wait for; default: the first Expect
#   Expect   regexes; each must match at least one output line
#   Forbid   regexes; none may match any output line
#   Check    scriptblock taking the result (.Texts); returns $null or the failure reason
#   AllowErrors   do not fail on error lines the mod wrote
function New-Step {
    param([string]$Cmd = '', [string]$Log = '', [string]$Until = '', [string[]]$Expect = @(), [string[]]$Forbid = @(),
          [scriptblock]$Check = $null, [switch]$AllowErrors)
    @{ Cmd = $Cmd; Log = $Log; Until = $Until; Expect = $Expect; Forbid = $Forbid; Check = $Check; AllowErrors = [bool]$AllowErrors }
}

# A row is a named check made of steps, run in order. Finally lists commands that always run afterwards (cleanup).
#   New-Row 'name' -Steps @(...) [-Finally @('awe reset x')]     steps checked one after the other
#   New-Row 'name' -Run { param($Slot) ... }                      free-form; returns $null or the failure reason
#   New-Row 'name' -Pending                                        placeholder the lead fills; reported as SKIP
function New-Row {
    param([string]$Name, [hashtable[]]$Steps = @(), [string[]]$Finally = @(), [scriptblock]$Run = $null, [switch]$Pending)
    @{ Name = $Name; Steps = $Steps; Finally = $Finally; Run = $Run; Pending = [bool]$Pending }
}

# Runs one step. Returns $null when it holds, else the reason.
function Test-Step {
    param([string]$Slot, [hashtable]$Step)
    if ($Step.Log) {
        $r = Get-LogLines -Slot $Slot -Grep $Step.Log
        if ($r.Exit -ne 0) { return "log read failed: $($r.Lines -join ' | ')" }
        $texts = $r.Lines
        $label = "log '$($Step.Log)'"
        $result = $null
    }
    else {
        $until = $Step.Until
        if (-not $until -and $Step.Expect.Count -gt 0) { $until = $Step.Expect[0] }
        $result = Invoke-Awe -Slot $Slot -Cmd $Step.Cmd -Until $until
        # Exit 1 is a bridge failure; exit 2 only says an error line or a missed -Until, both judged below.
        if ($result.Exit -eq 1 -or ($result.Exit -ne 0 -and $result.Exit -ne 2)) {
            return "'$($Step.Cmd)' failed to run: $($result.Raw -join ' | ')"
        }
        $texts = $result.Texts
        $label = "'$($Step.Cmd)'"
        if (-not $Step.AllowErrors -and $result.ModErrors.Count -gt 0) {
            return "$label logged an error: $($result.ModErrors -join ' | ')"
        }
    }
    foreach ($rx in $Step.Expect) {
        if (-not ($texts | Where-Object { $_ -match $rx })) {
            $shown = ($texts | Select-Object -Last 4) -join ' | '
            return "$label output has no line matching /$rx/ (last lines: $shown)"
        }
    }
    foreach ($rx in $Step.Forbid) {
        $hit = $texts | Where-Object { $_ -match $rx } | Select-Object -Last 1
        if ($hit) { return "$label output matches forbidden /$rx/: $hit" }
    }
    if ($Step.Check) {
        $why = & $Step.Check $result ($texts)
        if ($why) { return "$label $why" }
    }
    $null
}

# Runs one row. Returns $null when it holds, else the reason (cleanup problems are appended).
function Test-Row {
    param([string]$Slot, [hashtable]$Row)
    $reason = $null
    try {
        if ($Row.Run) { $reason = & $Row.Run $Slot }
        else {
            foreach ($step in $Row.Steps) {
                $reason = Test-Step -Slot $Slot -Step $step
                if ($reason) { break }
            }
        }
    }
    catch { $reason = "exception: $($_.Exception.Message)" }

    foreach ($cmd in $Row.Finally) {
        try {
            $c = Invoke-Awe -Slot $Slot -Cmd $cmd
            if ($c.Exit -eq 1) { $reason = (($reason, "cleanup '$cmd' failed: $($c.Raw -join ' | ')") | Where-Object { $_ }) -join '; ' }
        }
        catch { $reason = (($reason, "cleanup '$cmd' threw: $($_.Exception.Message)") | Where-Object { $_ }) -join '; ' }
    }
    $reason
}

# Prints the PASS/FAIL/SKIP line of one check and counts it.
function Write-Result {
    param([string]$Name, [string]$Reason, [switch]$Skip)
    if ($Skip) { Write-Host "SKIP $Name`: $Reason"; $script:Skipped++ }
    elseif ($Reason) { Write-Host "FAIL $Name`: $Reason"; $script:Failed++ }
    else { Write-Host "PASS $Name"; $script:Passed++ }
}

$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0
