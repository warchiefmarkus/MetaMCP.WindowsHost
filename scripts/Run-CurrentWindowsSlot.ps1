[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$currentPath = Join-Path $root 'current.json'

if (-not (Test-Path $currentPath)) {
    throw "A/B state file is missing: $currentPath. Build and deploy a Windows slot first."
}

$state = Get-Content $currentPath -Raw | ConvertFrom-Json
if ($state.activeSlot -notin @('A','B')) {
    throw "Invalid activeSlot in current.json: $($state.activeSlot)"
}

$expectedBase = Join-Path $root ("Release$($state.activeSlot)\win-x64")
$expectedExe = Join-Path $expectedBase 'MetaMCP.exe'
$configuredExe = [string]$state.activeExecutable
if ([string]::IsNullOrWhiteSpace($configuredExe) -or
    -not [IO.Path]::GetFullPath($configuredExe).Equals([IO.Path]::GetFullPath($expectedExe), [StringComparison]::OrdinalIgnoreCase)) {
    throw "current.json activeExecutable does not match activeSlot $($state.activeSlot)."
}
if (-not (Test-Path $expectedExe)) {
    throw "Active A/B executable is missing: $expectedExe"
}

Start-Process -FilePath $expectedExe -WorkingDirectory $expectedBase
Write-Host "Started active slot $($state.activeSlot): $expectedExe"
