[CmdletBinding()]
param(
    [string]$Repository = 'C:\DEV\LLM\metamcp',
    [switch]$InstallDependencies,
    [switch]$BuildOnly,
    [switch]$SwapOnly
)

$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'Build-WindowsCandidate.ps1'
$swap = Join-Path $PSScriptRoot 'Swap-WindowsRuntime.ps1'

if ($BuildOnly -and $SwapOnly) {
    throw '-BuildOnly and -SwapOnly cannot be used together.'
}

if (-not $SwapOnly) {
    $buildArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$build,'-Repository',$Repository)
    if ($InstallDependencies) { $buildArgs += '-InstallDependencies' }
    & powershell.exe @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Candidate build failed with exit code $LASTEXITCODE." }
    if ($BuildOnly) {
        Write-Host 'Candidate built; runtime swap was not requested.'
        exit 0
    }
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $swap
if ($LASTEXITCODE -ne 0) { throw "Runtime swap failed with exit code $LASTEXITCODE." }
