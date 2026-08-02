[CmdletBinding()]
param(
    [string]$Repository = 'C:\DEV\LLM\metamcp',
    [switch]$InstallDependencies
)

$ErrorActionPreference = 'Stop'
$hostRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$repositoryRoot = (Resolve-Path $Repository).Path
$slotNames = @('Release', 'Release2')

function Get-MetaMcpProcesses {
    Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith(
            $hostRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($_.ExecutablePath) -match '^MetaMCP(?:[.]next[0-9]*)?[.]exe$'
    }
}

function Get-ReleaseSlot([string]$executablePath) {
    $relative = $executablePath.Substring($hostRoot.Length).TrimStart('\', '/')
    $separatorChars = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $firstSegment = $relative.Split(
        $separatorChars,
        [System.StringSplitOptions]::RemoveEmptyEntries)[0]
    return $slotNames | Where-Object {
        $_.Equals($firstSegment, [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
}

$running = @(Get-MetaMcpProcesses)
$activeSlots = @($running | ForEach-Object {
    Get-ReleaseSlot $_.ExecutablePath
} | Where-Object { $_ } | Sort-Object -Unique)

if ($activeSlots.Count -gt 1) {
    throw "MetaMCP is running from both Release slots: $($activeSlots -join ', ')."
}

$activeSlot = $activeSlots | Select-Object -First 1
$targetSlot = if ($activeSlot -eq 'Release') {
    'Release2'
} else {
    'Release'
}
$targetBase = Join-Path $hostRoot $targetSlot
$targetOutput = Join-Path $targetBase 'win-x64'

$targetProcess = $running | Where-Object {
    (Get-ReleaseSlot $_.ExecutablePath) -eq $targetSlot
}
if ($targetProcess) {
    throw "Refusing to overwrite active slot $targetSlot (PID $($targetProcess.ProcessId))."
}

$activeProcess = $running | Select-Object -First 1
$activeBase = if ($activeProcess) {
    Split-Path $activeProcess.ExecutablePath -Parent
} else {
    $null
}

Write-Host "Active slot: $(if ($activeSlot) { $activeSlot } else { 'none' })"
Write-Host "Build target: $targetOutput"

$configBackup = $null
try {
    if ($activeBase -and (Test-Path (Join-Path $activeBase 'config'))) {
        $configBackup = Join-Path $env:TEMP (
            'metamcp-config-' + [guid]::NewGuid().ToString('N'))
        New-Item $configBackup -ItemType Directory -Force | Out-Null
        Copy-Item (Join-Path $activeBase 'config\*') $configBackup `
            -Recurse -Force -ErrorAction Stop
        Write-Host "Preserved config from: $activeBase"
    }

    $arguments = @(
        'run',
        '--project', (Join-Path $hostRoot 'src\MetaMCP.Packager'),
        '-c', 'Release',
        '--',
        '--repo', $repositoryRoot,
        '--target', 'win-x64',
        '--output', $targetOutput
    )
    if (-not $InstallDependencies) {
        $arguments += '--skip-install'
    }

    Push-Location $hostRoot
    try {
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Packager exited with code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }

    if ($configBackup) {
        $targetConfig = Join-Path $targetOutput 'config'
        New-Item $targetConfig -ItemType Directory -Force | Out-Null
        Copy-Item (Join-Path $configBackup '*') $targetConfig `
            -Recurse -Force -ErrorAction Stop
        Write-Host "Restored config to: $targetConfig"
    }

    $targetExe = Join-Path $targetOutput 'MetaMCP.exe'
    $manifest = Join-Path $targetOutput 'build-manifest.json'
    if (-not (Test-Path $targetExe)) {
        throw "Package validation failed: $targetExe is missing."
    }
    if (-not (Test-Path $manifest)) {
        throw "Package validation failed: $manifest is missing."
    }

    $metadata = [ordered]@{
        builtAt = (Get-Date).ToString('o')
        activeSlotAtBuild = if ($activeSlot) { $activeSlot } else { $null }
        targetSlot = $targetSlot
        sourceRepository = $repositoryRoot
        executable = $targetExe
    }
    $metadata | ConvertTo-Json | Set-Content `
        (Join-Path $targetBase 'release-slot.json') -Encoding UTF8

    $hash = (Get-FileHash $targetExe -Algorithm SHA256).Hash
    Write-Host "Package ready: $targetExe"
    Write-Host "SHA-256: $hash"
} finally {
    if ($configBackup -and (Test-Path $configBackup)) {
        Remove-Item $configBackup -Recurse -Force
    }
}
