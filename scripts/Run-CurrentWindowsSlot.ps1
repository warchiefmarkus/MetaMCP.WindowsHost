[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$currentPath = Join-Path $root 'current.json'

$exe = $null
if (Test-Path $currentPath) {
    $state = Get-Content $currentPath -Raw | ConvertFrom-Json
    if ($state.activeExecutable -and (Test-Path ([string]$state.activeExecutable))) {
        $exe = [string]$state.activeExecutable
    }
}

if (-not $exe) {
    foreach ($candidate in @(
        (Join-Path $root 'ReleaseB\win-x64\MetaMCP.exe'),
        (Join-Path $root 'ReleaseA\win-x64\MetaMCP.exe'),
        (Join-Path $root 'Release2\win-x64\MetaMCP.exe'),
        (Join-Path $root 'Release\win-x64\MetaMCP.exe')
    )) {
        if (Test-Path $candidate) { $exe = $candidate; break }
    }
}

if (-not $exe) { throw 'No runnable Windows MetaMCP release was found.' }
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
Write-Host "Started: $exe"
