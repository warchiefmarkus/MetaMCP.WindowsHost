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

function Get-HostProcesses {
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($_.ExecutablePath) -match '^MetaMCP(?:[.]next|[.]prev)?[.]exe$'
    })
}

function Get-LogicalSlot([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    foreach ($slot in 'A','B') {
        if ($path.StartsWith($slotPaths[$slot], [StringComparison]::OrdinalIgnoreCase)) { return $slot }
    }
    return $null
}

function Merge-JsonObject($defaults, $preserved) {
    if ($null -eq $preserved) { return $defaults }
    foreach ($property in $preserved.PSObject.Properties) {
        $existing = $defaults.PSObject.Properties[$property.Name]
        if ($existing -and $existing.Value -is [pscustomobject] -and $property.Value -is [pscustomobject]) {
            Merge-JsonObject $existing.Value $property.Value | Out-Null
        } elseif ($existing) {
            $existing.Value = $property.Value
        } else {
            $defaults | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value
        }
    }
    return $defaults
}

function Copy-PreservedRuntimeState([string]$sourceBase, [string]$targetBase) {
    if ([string]::IsNullOrWhiteSpace($sourceBase) -or -not (Test-Path $sourceBase)) { return }
    $sourceConfig = Join-Path $sourceBase 'config'
    $targetConfig = Join-Path $targetBase 'config'
    if (Test-Path $sourceConfig) {
        New-Item $targetConfig -ItemType Directory -Force | Out-Null
        Get-ChildItem $sourceConfig -Force | Where-Object Name -ne 'host.json' |
            Copy-Item -Destination $targetConfig -Recurse -Force
        $sourceHost = Join-Path $sourceConfig 'host.json'
        $targetHost = Join-Path $targetConfig 'host.json'
        if ((Test-Path $sourceHost) -and (Test-Path $targetHost)) {
            $defaults = Get-Content $targetHost -Raw | ConvertFrom-Json
            $preserved = Get-Content $sourceHost -Raw | ConvertFrom-Json
            $merged = Merge-JsonObject $defaults $preserved
            [IO.File]::WriteAllText($targetHost, ($merged | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
        }
    }
    $sourceData = Join-Path $sourceBase 'data'
    $targetData = Join-Path $targetBase 'data'
    if (Test-Path $sourceData) {
        New-Item $targetData -ItemType Directory -Force | Out-Null
        Copy-Item (Join-Path $sourceData '*') $targetData -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$running = Get-HostProcesses
if ($running.Count -gt 1) {
    $paths = $running | ForEach-Object { "$($_.ProcessId):$($_.ExecutablePath)" }
    throw "More than one MetaMCP host is running under repository root: $($paths -join '; ')"
}

$activeProcess = $running | Select-Object -First 1
$activeExe = if ($activeProcess) { $activeProcess.ExecutablePath } else { $null }
$activeBase = if ($activeExe) { Split-Path $activeExe -Parent } else { $null }
$activeSlot = Get-LogicalSlot $activeBase

if (-not $activeSlot -and (Test-Path $currentPath)) {
    try {
        $current = Get-Content $currentPath -Raw | ConvertFrom-Json
        if ($current.activeSlot -in @('A','B')) {
            $activeSlot = [string]$current.activeSlot
            if (-not $activeBase -and $current.activePath) { $activeBase = [string]$current.activePath }
            if (-not $activeExe -and $current.activeExecutable) { $activeExe = [string]$current.activeExecutable }
        }
    } catch {
        Write-Warning "Ignoring invalid current.json: $($_.Exception.Message)"
    }
}

# First migration from legacy Release/Release2 intentionally lands on B.
$targetSlot = if ($activeSlot -eq 'B') { 'A' } else { 'B' }
$targetBase = $slotPaths[$targetSlot]
$targetExe = Join-Path $targetBase 'MetaMCP.exe'

$targetRunning = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($targetBase, [StringComparison]::OrdinalIgnoreCase)
})
if ($targetRunning.Count -gt 0) {
    throw "Refusing to overwrite active candidate slot $targetSlot. PIDs: $($targetRunning.ProcessId -join ', ')"
}

Write-Host "Active host: $(if($activeExe){$activeExe}else{'none'})"
Write-Host "Logical active slot: $(if($activeSlot){$activeSlot}else{'legacy/none'})"
Write-Host "Candidate slot: $targetSlot"
Write-Host "Candidate output: $targetBase"

$packArgs = @(
    'run', '--project', (Join-Path $root 'src\MetaMCP.Packager'), '-c', 'Release', '--',
    '--repo', ([IO.Path]::GetFullPath($Repository)), '--target', 'win-x64', '--output', $targetBase
)
if (-not $InstallDependencies) { $packArgs += '--skip-install' }

Push-Location $root
try {
    & dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "MetaMCP packager failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

Copy-PreservedRuntimeState $activeBase $targetBase

foreach ($required in @(
    $targetExe,
    (Join-Path $targetBase 'build-manifest.json'),
    (Join-Path $targetBase 'config\host.json'),
    (Join-Path $targetBase 'config\.env.local'),
    (Join-Path $targetBase 'runtime\node\node.exe'),
    (Join-Path $targetBase 'metamcp\backend\dist\index.js'),
    (Join-Path $targetBase 'metamcp\frontend\server.js')
)) {
    if (-not (Test-Path $required)) { throw "Candidate validation failed: missing $required" }
}

$manifest = Get-Content (Join-Path $targetBase 'build-manifest.json') -Raw | ConvertFrom-Json
$pending = [ordered]@{
    schemaVersion = 1
    createdAt = (Get-Date).ToString('o')
    candidateSlot = $targetSlot
    candidatePath = $targetBase
    candidateExecutable = $targetExe
    candidateBuiltAt = $manifest.builtAt
    sourceRepository = [IO.Path]::GetFullPath($Repository)
    previousSlot = $activeSlot
    previousPath = $activeBase
    previousExecutable = $activeExe
    previousProcessId = if ($activeProcess) { [int]$activeProcess.ProcessId } else { $null }
}
$tmp = "$pendingPath.tmp"
[IO.File]::WriteAllText($tmp, ($pending | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Move-Item $tmp $pendingPath -Force

Write-Host "Candidate ready: $targetExe"
Write-Host "Pending state: $pendingPath"
Write-Host "NEXT: run scripts\Switch-WindowsSlot.ps1 from an external/delayed cmd process."
