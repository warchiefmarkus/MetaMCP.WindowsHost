[CmdletBinding()]
param(
    [int]$HealthTimeoutSeconds = 90,
    [switch]$KeepPendingOnSuccess
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pendingPath = Join-Path $root 'pending-update.json'
$currentPath = Join-Path $root 'current.json'
$logPath = Join-Path $root 'work\windows-ab-switch.log'
New-Item (Split-Path $logPath -Parent) -ItemType Directory -Force | Out-Null

function Log([string]$message) {
    "$(Get-Date -Format o) $message" | Add-Content $logPath
}

function Stop-ProcessesUnder([string]$basePath) {
    if ([string]::IsNullOrWhiteSpace($basePath)) { return }
    $normalized = [IO.Path]::GetFullPath($basePath).TrimEnd('\') + '\'
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($normalized, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($process in $processes | Sort-Object { if ($_.Name -like 'MetaMCP*') { 0 } else { 1 } }) {
        try {
            Log "Stopping PID=$($process.ProcessId) $($process.Name) $($process.ExecutablePath)"
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        } catch {
            Log "Stop failed PID=$($process.ProcessId): $($_.Exception.Message)"
        }
    }
}

function Stop-AllMetaMcpHostsExcept([string]$exceptBase) {
    $except = if ($exceptBase) { [IO.Path]::GetFullPath($exceptBase).TrimEnd('\') + '\' } else { $null }
    Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($_.ExecutablePath) -match '^MetaMCP(?:[.]next|[.]prev)?[.]exe$' -and
        (-not $except -or -not $_.ExecutablePath.StartsWith($except, [StringComparison]::OrdinalIgnoreCase))
    } | ForEach-Object {
        try {
            Log "Stopping host PID=$($_.ProcessId) $($_.ExecutablePath)"
            Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
        } catch {
            Log "Host stop failed PID=$($_.ProcessId): $($_.Exception.Message)"
        }
    }
}

function Wait-PortsFree([int]$seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $busy = @(Get-NetTCPConnection -State Listen -LocalPort 12008,12009 -ErrorAction SilentlyContinue)
        if ($busy.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Wait-Health([System.Diagnostics.Process]$process, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        if ($process.HasExited) {
            Log "Host exited early code=$($process.ExitCode)"
            return $false
        }
        try {
            $backend = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:12009/health' -TimeoutSec 2
            $frontend = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:12008/en' -TimeoutSec 2
            if ($backend.StatusCode -eq 200 -and $frontend.StatusCode -ge 200 -and $frontend.StatusCode -lt 500) {
                return $true
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Write-CurrentState($pending, [string]$candidateExe) {
    $state = [ordered]@{
        schemaVersion = 1
        activeSlot = [string]$pending.candidateSlot
        activePath = [string]$pending.candidatePath
        activeExecutable = $candidateExe
        previousSlot = $pending.previousSlot
        previousPath = $pending.previousPath
        previousExecutable = $pending.previousExecutable
        activatedAt = (Get-Date).ToString('o')
        candidateBuiltAt = $pending.candidateBuiltAt
        sourceRepository = $pending.sourceRepository
    }
    $tmp = "$currentPath.tmp"
    [IO.File]::WriteAllText($tmp, ($state | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    Move-Item $tmp $currentPath -Force
}

function Update-Shortcuts([string]$exe) {
    $ws = New-Object -ComObject WScript.Shell
    foreach ($shortcut in @(
        (Join-Path $root 'MetaMCP Release.lnk'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MetaMCP.lnk')
    )) {
        if (-not (Test-Path $shortcut)) { continue }
        $s = $ws.CreateShortcut($shortcut)
        $s.TargetPath = $exe
        $s.WorkingDirectory = Split-Path $exe -Parent
        $s.IconLocation = "$exe,0"
        $s.Save()
    }
}

function Prepare-LegacyRollbackSlot($pending) {
    if ($pending.previousSlot -in @('A','B')) { return $pending }
    if ([string]::IsNullOrWhiteSpace([string]$pending.previousPath) -or
        [string]::IsNullOrWhiteSpace([string]$pending.previousExecutable) -or
        -not (Test-Path ([string]$pending.previousPath)) -or
        -not (Test-Path ([string]$pending.previousExecutable))) {
        return $pending
    }

    $rollbackSlot = if ([string]$pending.candidateSlot -eq 'B') { 'A' } else { 'B' }
    $rollbackBase = Join-Path $root ("Release$rollbackSlot\\win-x64")
    $rollbackExe = Join-Path $rollbackBase 'MetaMCP.exe'
    if (Test-Path $rollbackBase) {
        $running = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath.StartsWith($rollbackBase, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($running.Count -gt 0) {
            throw "Cannot seed rollback slot $rollbackSlot while it is running."
        }
        Remove-Item $rollbackBase -Recurse -Force
    }
    New-Item $rollbackBase -ItemType Directory -Force | Out-Null

    $sourceBase = [string]$pending.previousPath
    Log "Seeding rollback slot $rollbackSlot from legacy runtime $sourceBase"
    & robocopy.exe $sourceBase $rollbackBase /MIR /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $robocopyExit = $LASTEXITCODE
    if ($robocopyExit -gt 7) {
        throw "Failed to seed rollback slot $rollbackSlot; robocopy exit code $robocopyExit."
    }

    $previousName = [IO.Path]::GetFileName([string]$pending.previousExecutable)
    $copiedPreviousExe = Join-Path $rollbackBase $previousName
    if (-not (Test-Path $copiedPreviousExe)) {
        throw "Rollback executable was not copied: $copiedPreviousExe"
    }
    if (-not $copiedPreviousExe.Equals($rollbackExe, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item $copiedPreviousExe $rollbackExe -Force
    }
    Get-ChildItem $rollbackBase -Filter 'MetaMCP.*.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { -not $_.FullName.Equals($rollbackExe, [StringComparison]::OrdinalIgnoreCase) } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $pending.previousSlot = $rollbackSlot
    $pending.previousPath = $rollbackBase
    $pending.previousExecutable = $rollbackExe
    Log "Rollback slot seeded: $rollbackSlot -> $rollbackExe"
    return $pending
}

function Cleanup-LegacyWindowsSlots($pending) {
    if ($pending.previousSlot -notin @('A','B')) { return }
    foreach ($legacy in @(
        (Join-Path $root 'Release\\win-x64'),
        (Join-Path $root 'Release2\\win-x64')
    )) {
        if (-not (Test-Path $legacy)) { continue }
        $running = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath.StartsWith($legacy, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($running.Count -gt 0) {
            Log "Legacy cleanup skipped; processes still run under $legacy"
            continue
        }
        try {
            Remove-Item $legacy -Recurse -Force
            Log "Legacy Windows slot removed: $legacy"
        } catch {
            Log "Legacy cleanup failed for ${legacy}: $($_.Exception.Message)"
        }
    }
}

if (-not (Test-Path $pendingPath)) { throw "No pending update: $pendingPath" }
$pending = Get-Content $pendingPath -Raw | ConvertFrom-Json
$candidateBase = [string]$pending.candidatePath
$candidateExe = [string]$pending.candidateExecutable
$previousBase = [string]$pending.previousPath
$previousExe = [string]$pending.previousExecutable

if (-not (Test-Path $candidateExe)) { throw "Candidate executable does not exist: $candidateExe" }

Log "switch begin candidate=$($pending.candidateSlot) exe=$candidateExe previous=$previousExe"

Stop-AllMetaMcpHostsExcept $candidateBase
Stop-ProcessesUnder $previousBase
if (-not (Wait-PortsFree 20)) {
    Log 'Ports 12008/12009 are still busy; terminating owners under repository root.'
    foreach ($connection in @(Get-NetTCPConnection -State Listen -LocalPort 12008,12009 -ErrorAction SilentlyContinue)) {
        $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($connection.OwningProcess)" -ErrorAction SilentlyContinue
        if ($owner -and $owner.ExecutablePath -and
            $owner.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $owner.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Milliseconds 700
}

$candidate = Start-Process -FilePath $candidateExe -WorkingDirectory $candidateBase -PassThru
Log "candidate started PID=$($candidate.Id)"

if (Wait-Health $candidate $HealthTimeoutSeconds) {
    $pending = Prepare-LegacyRollbackSlot $pending
    Write-CurrentState $pending $candidateExe
    Update-Shortcuts $candidateExe
    Cleanup-LegacyWindowsSlots $pending
    if (-not $KeepPendingOnSuccess) {
        Remove-Item $pendingPath -Force -ErrorAction SilentlyContinue
    }
    Log "switch success active=$($pending.candidateSlot) PID=$($candidate.Id)"
    exit 0
}

Log 'candidate health failed; rollback begin'
Stop-ProcessesUnder $candidateBase
if (-not [string]::IsNullOrWhiteSpace($previousExe) -and (Test-Path $previousExe)) {
    $previous = Start-Process -FilePath $previousExe -WorkingDirectory (Split-Path $previousExe -Parent) -PassThru
    Log "rollback started PID=$($previous.Id) exe=$previousExe"
    if (Wait-Health $previous $HealthTimeoutSeconds) {
        Log 'rollback healthy'
        exit 2
    }
    Log 'rollback health failed'
}
throw 'MetaMCP candidate failed health check and rollback was not confirmed healthy.'
