<#
Employee capacity next to a mod that changes it (Based raises the Sweatshop's capacity after the save loaded).

    pwsh -File tests/awe-capacity-guard.ps1 -Slot mptest4 [-Property Sweatshop]

Needs a Debug build of AWE. With Based and its Sweatshop save (session.ps1 -Template mrmonkey-dock -Mods Based,MapExpansions)
the checks cover the real case; on any other save -Property names a property whose capacity the script changes itself.
'awe capacity' prints capacity, employees and the remembered vanilla capacity per property.
  no-guard-warn  the log has no "keeps capacity" warning from the loss guard since the save loaded
  follows-mod    the property's remembered vanilla capacity equals its live capacity (the other mod's value)
  foreign-set    'awe capacity <property> <n>' (as another mod) is taken over as the new vanilla value, logged as info
  override-on    override on with capacity 12: the property has 12
  override-off   override off: the property goes back to the other mod's value
Resets the options at the end. Prints PASS or FAIL per check and a SUMMARY line.
#>
param(
    [Parameter(Mandatory)][string]$Slot,
    [string]$Property = 'Sweatshop'
)
$s1 = 'F:\Projects\Mods\Schedule1\Workspace\tools\bridge\s1.py'
$pass = 0; $fail = 0
function Result([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "PASS $name $detail" } else { $script:fail++; Write-Host "FAIL $name $detail" }
}
function Run([string]$cmd) { (& python $s1 --slot $Slot run $cmd 2>&1 | Out-String) }
function Cap {
    $out = Run 'awe capacity'
    $m = [regex]::Matches($out, "\[Capacity\] (\S*$([regex]::Escape($Property))\S*) capacity=(\d+) employees=(\d+) vanilla=(\d+) overridden=(\d)")
    if ($m.Count -eq 0) { Write-Host "  no property matching '$Property':"; Write-Host $out; return $null }
    $g = $m[0].Groups
    [pscustomobject]@{ Name = $g[1].Value; Cap = [int]$g[2].Value; Emp = [int]$g[3].Value; Van = [int]$g[4].Value; Ours = [int]$g[5].Value; Line = $m[0].Value }
}

$log = (& python $s1 --slot $Slot log -n 4000 2>&1 | Out-String)
$warn = @($log -split "`n" | Where-Object { $_ -match 'keeps capacity' })
Result ($warn.Count -eq 0) 'no-guard-warn' $(if ($warn.Count) { $warn[-1].Trim() } else { 'none in the log' })
$info = @($log -split "`n" | Where-Object { $_ -match "\[bal-property\] .*$([regex]::Escape($Property)).*set by the game or another mod" })
if ($info.Count) { Write-Host "  NOTE $($info[-1].Trim())" }

$c = Cap
if (-not $c) { Write-Host "SUMMARY $pass pass, $($fail + 1) fail"; exit 1 }
Result ($c.Van -eq $c.Cap -and $c.Ours -eq 0) 'follows-mod' $c.Line
$modValue = $c.Cap

$set = $modValue + 2
$null = Run "awe capacity $($c.Name) $set"
Start-Sleep -Seconds 2
$c = Cap
$log = (& python $s1 --slot $Slot log -n 200 2>&1 | Out-String)
$taken = $log -match "\[bal-property\] $([regex]::Escape($c.Name)): capacity is now $set, set by the game or another mod"
Result ($c.Cap -eq $set -and $c.Van -eq $set -and $taken) 'foreign-set' "$($c.Line) logged=$taken"
$modValue = $set

$null = Run 'awe set property.enableemployeecapacityoverride on'
$null = Run 'awe set property.employeecapacity 12'
Start-Sleep -Seconds 2
$c = Cap
Result ($c.Cap -eq 12 -and $c.Ours -eq 1) 'override-on' $c.Line

$null = Run 'awe set property.enableemployeecapacityoverride off'
Start-Sleep -Seconds 2
$c = Cap
Result ($c.Cap -eq $modValue -and $c.Ours -eq 0) 'override-off' "$($c.Line) expected=$modValue"

$null = Run 'awe reset property.employeecapacity'
$null = Run 'awe reset property.enableemployeecapacityoverride'
Write-Host "SUMMARY $pass pass, $fail fail"
if ($fail -gt 0) { exit 1 }
