[CmdletBinding()]
param(
    [ValidateSet('A','B')]
    [string]$Slot,
    [switch]$Status,
    [string]$Root
)

$ErrorActionPreference = 'Stop'
# PowerShell 5.1 can evaluate parameter defaults before $PSScriptRoot is
# populated. Resolve the repository root only after entering the script body.
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
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

if (-not $Status) {
    # A/B switching requires the stable Windows Host gateway. On a new
    # installation current.json does not exist until the first successful swap,
    # so start the old host from ReleaseA rather than fabricating slot metadata.
    $hostExe = Join-Path $root 'MetaMCP.exe'
    if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) {
        throw "Stable MetaMCP Host executable not found: $hostExe"
    }
    $running = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -eq 'MetaMCP.exe' -and $_.ExecutablePath -and
        $_.ExecutablePath.Equals($hostExe, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($running.Count -eq 0) {
        Write-Host "Starting MetaMCP Host to perform A/B swap: $hostExe"
        Start-Process -FilePath $hostExe -WorkingDirectory $root -ArgumentList "--base `"$root`""
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $ready = $false
    $lastErrorMessage = 'no response'
    do {
        try {
            $null = Invoke-RestMethod -Method Get -Uri "$base/status" -Headers $headers -TimeoutSec 3 -ErrorAction Stop
            $ready = $true
            break
        }
        catch {
            $lastErrorMessage = $_.Exception.Message
            if ($_.Exception.Response) {
                throw "MetaMCP Host control endpoint rejected status check: $lastErrorMessage"
            }
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) {
        $logPath = Join-Path $root 'logs\host.log'
        $recentLog = if (Test-Path $logPath) {
            (Get-Content $logPath -Tail 16) -join [Environment]::NewLine
        } else { 'Host log not found.' }
        throw "MetaMCP Host control endpoint did not become ready at $base. $lastErrorMessage`n$recentLog"
    }
    Write-Host 'MetaMCP Host control endpoint ready.'
}

if ($Status) {
    $result = Invoke-RestMethod -Method Get -Uri "$base/status" -Headers $headers -TimeoutSec 10
} else {
    $body = @{}
    if ($Slot) { $body.slot = $Slot }
    $result = Invoke-RestMethod -Method Post -Uri "$base/swap" -Headers $headers `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 180
}

$result | ConvertTo-Json -Depth 20
