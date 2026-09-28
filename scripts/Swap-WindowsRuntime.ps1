[CmdletBinding()]
param(
    [ValidateSet('A','B')]
    [string]$Slot,
    [switch]$Status,
    [string]$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Root)
$configPath = Join-Path $root 'config\host.json'
if (-not (Test-Path $configPath)) {
    throw "Stable bootstrapper config is missing: $configPath"
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
$backendPort = [int]$config.BackendPort
$token = [string]$config.HostControlToken
if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'HostControlToken is missing from config\host.json.'
}

$headers = @{ 'X-MetaMCP-Host-Control-Token' = $token }
$base = "http://127.0.0.1:$backendPort/__host/runtime"

if ($Status) {
    $result = Invoke-RestMethod -Method Get -Uri "$base/status" -Headers $headers -TimeoutSec 10
} else {
    $body = @{}
    if ($Slot) { $body.slot = $Slot }
    $result = Invoke-RestMethod -Method Post -Uri "$base/swap" -Headers $headers `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 180
}

$result | ConvertTo-Json -Depth 20
