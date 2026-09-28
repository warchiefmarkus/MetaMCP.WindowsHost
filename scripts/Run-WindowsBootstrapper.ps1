[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = Join-Path $root 'MetaMCP.exe'
if (-not (Test-Path $exe)) {
    throw "Stable bootstrapper executable is missing: $exe"
}
Start-Process -FilePath $exe -WorkingDirectory $root -ArgumentList @('--base', $root)
Write-Host "Started MetaMCP bootstrapper: $exe"
