<#
Static check on the built mod DLL: no member reference to the one-argument Newtonsoft ToString(Formatting) remains.
MelonLoader 0.7.0 ships a Newtonsoft.Json without that overload; ToString(Formatting, params JsonConverter[]) exists everywhere.

    pwsh -File tests/awe-no-tostring-formatting.ps1 [-Dll <path>]

Without -Dll the Release build of this repo is built (compile only) and checked.
#>
param([string]$Dll)
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Dll) {
    & dotnet build (Join-Path $repo 'ActuallyWorkingEmployees.csproj') -c Release -p:DeployVerifyDisable=true -v q -nologo | Out-Host
    $Dll = Get-ChildItem (Join-Path $repo 'bin') -Recurse -Filter ActuallyWorkingEmployees.dll | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $Dll -or -not (Test-Path $Dll)) { Write-Host "FAIL dll-found no built DLL ($Dll)"; exit 1 }
Write-Host "checking $Dll"
Add-Type -AssemblyName System.Reflection.Metadata
$bytes = [System.IO.File]::ReadAllBytes($Dll)
$pe = [System.Reflection.PortableExecutable.PEReader]::new([System.Collections.Immutable.ImmutableArray]::Create($bytes))
$md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
$toString = 0; $oneArg = @()
foreach ($h in $md.MemberReferences) {
    $m = $md.GetMemberReference($h)
    if ($md.GetString($m.Name) -ne 'ToString' -or $m.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
    $t = $md.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$m.Parent)
    $ns = $md.GetString($t.Namespace)
    if ($ns -notlike 'Newtonsoft.Json*') { continue }
    $toString++
    $r = $md.GetBlobReader($m.Signature)
    [void]$r.ReadByte()                      # calling convention
    $count = $r.ReadCompressedInteger()      # parameter count
    $name = "$ns.$($md.GetString($t.Name)).ToString/$count"
    Write-Host "  ref $name"
    if ($count -eq 1) { $oneArg += $name }
}
if ($oneArg.Count -eq 0) { Write-Host "PASS no-one-arg-tostring $toString Newtonsoft ToString reference(s), none with one parameter"; exit 0 }
Write-Host "FAIL no-one-arg-tostring $($oneArg -join ', ')"; exit 1
