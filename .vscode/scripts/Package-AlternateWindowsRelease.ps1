[CmdletBinding()]
param(
    [string]$Repository = 'C:\DEV\LLM\metamcp',
    [switch]$InstallDependencies
)

# Compatibility wrapper. Canonical Windows packaging now builds the inactive A/B slot.
$script = Join-Path $PSScriptRoot '..\..\scripts\Build-WindowsCandidate.ps1'
$args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$script,'-Repository',$Repository)
if ($InstallDependencies) { $args += '-InstallDependencies' }
& powershell.exe @args
exit $LASTEXITCODE
