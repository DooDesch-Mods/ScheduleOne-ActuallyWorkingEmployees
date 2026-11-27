<#
The whip in a storage (AWE#68) and the patches moved off shared native code (AWE#54), on the host.

    pwsh -File tests/awe-whip-storage.ps1 -Slot mptest [-Shot] [-OutDir <dir>] [-BotanistSeconds 180] [-Only storage,log]

Needs the Debug build ('awe store', 'awe whip', 'awe native'). 'awe store put' makes the two calls of a player drop on an
empty slot (ItemUIManager.EndDrag): SetStoredItem on the storage slot, then ChangeQuantity on the inventory slot.
Checks:
  stored-model     the whip definition got a StoredItem copied from a vanilla tool, without the "no vanilla tool" warning.
  <store>-whip     for a closet and a rack: the whip moves into the storage and leaves the inventory, no exception.
  <store>-after    a vanilla item (3x cuke) put in after the whip moves too.
  <store>-reopen   after reopening, the storage holds both and the inventory neither.
  rack-drawn       the rack draws the whip model (AWEWhipStoredItem) among its contents.
  held-whip        a whip in hand moves into the closet and leaves the inventory.
  siblings         no game method shares native code with an AWE patch target.
  action-time      with changed botanist action times, a botanist action's wait is rescaled to wanted seconds / work speed
                   (within 0.05 s). Waits up to -BotanistSeconds for a botanist action.
  log              no [Failed] line and no failed store move in the log.
-Shot: screenshot of the rack with the whip, saved as whip-rack.png in -OutDir. Resets the options at the end. Prints PASS
or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [switch]$Shot,
    [string]$OutDir = (Join-Path $env:TEMP 'awe-whip-storage'),
    [int]$BotanistSeconds = 180,
    # Check groups to run: stored-model, storage (closet, rack, held whip, shot), siblings, action-time, log. Empty = all.
    [string[]]$Only
)
$s1 = Join-Path $PSScriptRoot '..\..\Workspace\tools\bridge\s1.py'
if (-not (Test-Path $s1)) { $s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py' }
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$env:PYTHONIOENCODING = 'utf-8'
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function S1 { param([Parameter(ValueFromRemainingArguments)][string[]]$A) (& python $s1 --slot $Slot @A 2>&1 | Out-String).Trim() }
function LastLine([string]$text, [string]$pattern) { ($text -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ -match $pattern } | Select-Object -Last 1) }
function Qty([string]$line, [string]$id) {
    $n = 0
    foreach ($m in [regex]::Matches([string]$line, '(?<![\w])' + [regex]::Escape($id) + 'x(\d+)')) { $n += [int]$m.Groups[1].Value }
    $n
}
# 'awe store count' prints the inventory line, then the storage line.
function Counts {
    $c = S1 run 'awe store count'
    [pscustomobject]@{ Inv = (LastLine $c '\[store\] inventory:'); Store = (LastLine $c '\[store\] storage '); }
}
function Put([string]$id) { S1 run "awe store put $id" }
function PutOk([string]$out) { ($out -match 'put: done') -and ($out -notmatch 'failed|Exception') }

New-Item -ItemType Directory -Force $OutDir | Out-Null
# 's1.py log --grep' matches a plain substring, so each pattern below is its own grep.
function LogLines([string]$text) { S1 log -n 400000 --grep $text }
function Want([string]$name) { -not $Only -or ($Only -contains $name) }

# -- stored-model --
if (Want 'stored-model') {
    $built = LastLine (LogLines '[Whip] stored model built from') 'stored model built from'
    $noTool = LastLine (LogLines '[Whip] no vanilla tool to copy the stored model') 'no vanilla tool'
    Result ([bool]$built -and -not $noTool) 'stored-model' $(if ($built) { $built.Trim() } elseif ($noTool) { $noTool.Trim() } else { 'no stored-model line in the log' })
}

# -- closet and rack: whip, then a vanilla item, then reopen --
$rackVisual = $null
foreach ($store in 'Medium Storage Closet', 'Medium Storage Rack') {
    if (-not (Want 'storage')) { break }
    $tag = if ($store -match 'Rack') { 'rack' } else { 'closet' }
    $o = S1 run "awe store open $store"
    if ($o -notmatch "opened $([regex]::Escape($store))") { Result $false "$tag-open" (LastLine $o '\[store\]'); continue }
    $g = S1 run 'awe whip give'
    $before = Counts
    if ((Qty $before.Inv 'awe_whip') -lt 1) { Result $false "$tag-whip" "no whip after 'awe whip give': $($before.Inv) / $(LastLine $g 'Whip')"; continue }
    $p = Put 'awe_whip'
    $a = Counts
    Result ((PutOk $p) -and (Qty $a.Inv 'awe_whip') -eq 0 -and (Qty $a.Store 'awe_whip') -eq 1) "$tag-whip" "$(LastLine $p 'put: done|failed') | $($a.Inv.Trim()) | $($a.Store.Trim())"

    S1 run 'give cuke 3' | Out-Null
    $p = Put 'cuke'
    $a = Counts
    Result ((PutOk $p) -and (Qty $a.Inv 'cuke') -eq 0 -and (Qty $a.Store 'cuke') -eq 3) "$tag-after" "$(LastLine $p 'put: done|failed') | $($a.Inv.Trim()) | $($a.Store.Trim())"

    S1 run 'awe store reopen' | Out-Null
    $a = Counts
    Result ((Qty $a.Inv 'awe_whip') -eq 0 -and (Qty $a.Inv 'cuke') -eq 0 -and (Qty $a.Store 'awe_whip') -eq 1 -and (Qty $a.Store 'cuke') -eq 3) "$tag-reopen" "$($a.Inv.Trim()) | $($a.Store.Trim())"

    if ($tag -eq 'rack') {
        $v = S1 run 'awe store visuals'
        $rackVisual = LastLine $v 'visual: AWEWhipStoredItem'
        Result ([bool]$rackVisual) 'rack-drawn' $(if ($rackVisual) { $rackVisual.Trim() } else { LastLine $v 'visuals: ' })
    }
}

# -- a whip in hand --
if (Want 'storage') {
    S1 run 'awe store open Medium Storage Closet' | Out-Null
    S1 run 'awe whip give' | Out-Null
    $h = S1 run 'awe whip hold'
    $p = Put 'awe_whip'
    $a = Counts
    Result ((PutOk $p) -and $p -match 'equipped=True' -and (Qty $a.Inv 'awe_whip') -eq 0) 'held-whip' "$(LastLine $p 'put: \d+x|failed') | $(LastLine $p 'put: done|failed') | $($a.Inv.Trim())"
}

if ($Shot -and $rackVisual -and $rackVisual -match 'at (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?)') {
    $x = [double]::Parse($Matches[1], $inv); $y = [double]::Parse($Matches[2], $inv); $z = [double]::Parse($Matches[3], $inv)
    $f = { param($n) $n.ToString('F2', $inv) }
    # Passed as one array: negative coordinates through the S1 helper do not reach python as values.
    $camArgs = @('cam', 'shot', '--target', (& $f $x), (& $f $y), (& $f $z), '--eye', (& $f ($x + 0.28)), (& $f ($y + 0.51)), (& $f ($z + 0.3)), '--hud', 'hide', '--fov', '40')
    $shotOut = (& python $s1 --slot $Slot @camArgs 2>&1 | Out-String)
    $png = [regex]::Match($shotOut, '[A-Za-z]:[\\/][^"]+?\.png').Value -replace '\\\\', '\'
    if ($png -and (Test-Path $png)) { $dst = Join-Path $OutDir 'whip-rack.png'; Copy-Item $png $dst -Force; Write-Host "  shot $dst" }
    else { Write-Host "  NOTE shot: no file ($((LastLine $shotOut '\S')))" }
    S1 cam release | Out-Null
}

# -- AWE#54: no shared native code under an AWE patch --
if (Want 'siblings') {
    $n = S1 run 'awe native siblings'
    $shared = @($n -split "`n" | Where-Object { $_ -match 'siblings: 0x\w+ AWE patches (.+); (\d+) method\(s\) share it' -and [int]$Matches[2] -gt 1 } | ForEach-Object { $_.Trim() })
    $sum = LastLine $n 'siblings: \d+ AWE address'
    Result ([bool]$sum -and $shared.Count -eq 0) 'siblings' $(if ($shared.Count) { $shared -join ' || ' } elseif ($sum) { $sum.Trim() } else { 'no summary line' })
}

# -- AWE#54: action times through PerformAction --
if (Want 'action-time') {
    $keys = 'botanist.soiltime', 'botanist.watertime', 'botanist.additivetime', 'botanist.sowtime', 'botanist.mushroombedtime'
    foreach ($k in $keys) { S1 run "awe set $k 3" | Out-Null }
    S1 run 'awe set botanist.harvesttimeperunit 2.5' | Out-Null
    S1 run 'awe set botanist.mushroomharvesttimeperunit 2.5' | Out-Null
    $deadline = (Get-Date).AddSeconds($BotanistSeconds)
    $line = $null
    $rx = 'PerformAction wait ([\d.]+) s -> ([\d.]+) s: vanilla ([\d.]+) s, wanted ([\d.]+) s, work speed x([\d.]+)'
    while ((Get-Date) -lt $deadline) {
        $line = LastLine (LogLines 'PerformAction wait') $rx
        if ($line) { break }
        Start-Sleep -Seconds 5
    }
    if ($line -and $line -match $rx) {
        $after = [double]::Parse($Matches[2], $inv); $want = [double]::Parse($Matches[4], $inv); $speed = [double]::Parse($Matches[5], $inv)
        Result ([math]::Abs($after - $want / $speed) -le 0.05) 'action-time' $line.Trim()
    }
    else {
        $warn = LastLine (LogLines '[bal-botanist]') 'PerformAction coroutine|left unchanged'
        Result $false 'action-time' "no rescaled wait within $BotanistSeconds s$(if ($warn) { "; $($warn.Trim())" })"
    }
    foreach ($k in $keys + 'botanist.harvesttimeperunit', 'botanist.mushroomharvesttimeperunit') { S1 run "awe reset $k" | Out-Null }
}

# -- log --
if (Want 'log') {
    $badLine = LastLine (LogLines '[Failed]') '\[Failed\]'
    if (-not $badLine) { $badLine = LastLine (LogLines '[Dev] [store]') '\[store\] \w+ failed' }
    Result (-not $badLine) 'log' $(if ($badLine) { $badLine.Trim() } else { 'no [Failed] line, no failed store move' })
}

Write-Host "SUMMARY slot=$Slot pass=$pass fail=$fail"
if ($fail) { exit 1 }
