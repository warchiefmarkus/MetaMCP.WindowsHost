[CmdletBinding()]
param(
    [string]$Repository = 'C:\DEV\LLM\metamcp',
    [switch]$InstallDependencies
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$currentPath = Join-Path $root 'current.json'
$pendingPath = Join-Path $root 'pending-update.json'
$slotPaths = @{
    A = Join-Path $root 'ReleaseA\win-x64'
    B = Join-Path $root 'ReleaseB\win-x64'
}

function Normalize-Slot([string]$slot) {
    $value = $slot.Trim().ToUpperInvariant()
    if ($value -notin @('A','B')) { throw "Invalid runtime slot: $slot" }
    return $value
}

function Get-ActiveSlot {
    if (Test-Path $currentPath) {
        $state = Get-Content $currentPath -Raw | ConvertFrom-Json
        if ($state.activeSlot -in @('A','B')) { return (Normalize-Slot ([string]$state.activeSlot)) }
    }
    if (Test-Path $slotPaths.A) { return 'A' }
    if (Test-Path $slotPaths.B) { return 'B' }
    return 'A'
}

$activeSlot = Get-ActiveSlot
$activeBase = $slotPaths[$activeSlot]
$targetSlot = if ($activeSlot -eq 'A') { 'B' } else { 'A' }
$targetBase = $slotPaths[$targetSlot]

$targetRunning = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith(
        ([IO.Path]::GetFullPath($targetBase).TrimEnd('\') + '\'),
        [StringComparison]::OrdinalIgnoreCase)
})
if ($targetRunning.Count -gt 0) {
    throw "Refusing to overwrite runtime slot $targetSlot while it is still running/draining. PIDs: $($targetRunning.ProcessId -join ', ')"
}

Write-Host "Active runtime slot: $activeSlot"
Write-Host "Candidate runtime slot: $targetSlot"
Write-Host "Candidate output: $targetBase"

$packArgs = @(
    'run', '--project', (Join-Path $root 'src\MetaMCP.Packager'), '-c', 'Release', '--',
    '--repo', ([IO.Path]::GetFullPath($Repository)), '--target', 'win-x64', '--output', $targetBase,
    '--runtime-only'
)
if (-not $InstallDependencies) { $packArgs += '--skip-install' }

Push-Location $root
try {
    & dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "MetaMCP packager failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

foreach ($required in @(
    (Join-Path $targetBase 'build-manifest.json'),
    (Join-Path $targetBase 'runtime\node\node.exe'),
    (Join-Path $targetBase 'metamcp\backend\dist\index.js'),
    (Join-Path $targetBase 'metamcp\frontend\server.js')
)) {
    if (-not (Test-Path $required)) { throw "Candidate validation failed: missing $required" }
}

$manifest = Get-Content (Join-Path $targetBase 'build-manifest.json') -Raw | ConvertFrom-Json
$pending = [ordered]@{
    schemaVersion = 2
    createdAt = (Get-Date).ToString('o')
    candidateSlot = $targetSlot
    candidatePath = $targetBase
    candidateBuiltAt = $manifest.builtAt
    sourceRepository = [IO.Path]::GetFullPath($Repository)
    previousSlot = $activeSlot
    previousPath = $activeBase
}
$tmp = "$pendingPath.tmp"
[IO.File]::WriteAllText($tmp, ($pending | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Move-Item $tmp $pendingPath -Force

Write-Host "Candidate ready: $targetSlot"
Write-Host "Pending state: $pendingPath"
Write-Host 'NEXT: scripts\Swap-WindowsRuntime.ps1'
