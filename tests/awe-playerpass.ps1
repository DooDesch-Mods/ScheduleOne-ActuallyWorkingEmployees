<#
Players pass through employees (general.playerspassthroughemployees).

    pwsh -File tests/awe-playerpass.ps1 -Slot mptest4

Needs a Debug build of AWE and a save with a hired employee (template 'played'). 'awe playerpass walk' puts the local
player 1.5 m in front of the nearest employee and moves its CharacterController 3 m through it.
  off-blocked    option off: the walk stops at the employee (through=False)
  on-pairs       option on: ignore pairs are set for at least one employee
  on-through     option on: the walk ends past the employee, or past it at a wall or object behind it (through=True)
  off-restored   option off again: the pairs are reset and the walk is blocked again
Resets the option at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param([Parameter(Mandatory)][string]$Slot)
$s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py'
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function Run([string]$cmd) { (& python $s1 --slot $Slot run $cmd 2>&1 | Out-String) }
function Walk([string]$out) {
    $m = [regex]::Match($out, 'walk .*?through=(True|False) option=(True|False)')
    if (-not $m.Success) { return $null }
    [pscustomobject]@{ Through = $m.Groups[1].Value -eq 'True'; Option = $m.Groups[2].Value -eq 'True'; Line = $m.Value }
}
function Status([string]$out) {
    $m = [regex]::Match($out, 'on=(\d) employees=(\d+) playerColliders=(\d+)')
    if (-not $m.Success) { return $null }
    [pscustomobject]@{ On = [int]$m.Groups[1].Value; Employees = [int]$m.Groups[2].Value; Player = [int]$m.Groups[3].Value }
}

$null = Run 'awe set general.playerspassthroughemployees false'
# The first walk after a load starts far from the employee, whose colliders can still be off in that frame.
# One walk first puts the player next to the employee; the measured walks start from there.
$null = Run 'awe playerpass walk'
Start-Sleep -Seconds 1
$w = Walk (Run 'awe playerpass walk')
Result ($w -and -not $w.Through -and -not $w.Option) 'off-blocked' $(if ($w) { $w.Line } else { 'no walk line' })

$null = Run 'awe set general.playerspassthroughemployees true'
$out = Run 'awe playerpass walk'
$st = Status $out
Result ($st -and $st.On -eq 1 -and $st.Employees -ge 1 -and $st.Player -ge 1) 'on-pairs' $(if ($st) { "on=$($st.On) employees=$($st.Employees) playerColliders=$($st.Player)" } else { 'no status line' })
$w = Walk $out
Result ($w -and $w.Through -and $w.Option) 'on-through' $(if ($w) { $w.Line } else { 'no walk line' })

$null = Run 'awe set general.playerspassthroughemployees false'
$out = Run 'awe playerpass walk'
$st = Status $out
$w = Walk $out
Result ($st -and $st.Employees -eq 0 -and $w -and -not $w.Through) 'off-restored' $(if ($w) { "employees=$($st.Employees) $($w.Line)" } else { 'no walk line' })

$null = Run 'awe reset general.playerspassthroughemployees'
Write-Host "SUMMARY $pass pass, $fail fail"
if ($fail -gt 0) { exit 1 }
