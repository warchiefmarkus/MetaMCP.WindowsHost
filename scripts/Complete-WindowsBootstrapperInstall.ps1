[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StagedExe,
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$currentPath = Join-Path $root 'current.json'
$destination = Join-Path $root 'MetaMCP.exe'
$backup = Join-Path $root 'MetaMCP.bootstrap.prev.exe'
$StagedExe = [IO.Path]::GetFullPath($StagedExe)
if (-not (Test-Path -LiteralPath $StagedExe -PathType Leaf)) {
    throw "Staged executable missing: $StagedExe"
}
$expectedHash = (Get-FileHash -LiteralPath $StagedExe -Algorithm SHA256).Hash
$originalExists = Test-Path -LiteralPath $destination -PathType Leaf

function Get-HostProcesses {
    @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -eq 'MetaMCP.exe' -and
        $_.ExecutablePath -and
        $_.ExecutablePath.Equals($destination, [StringComparison]::OrdinalIgnoreCase)
    })
}

function Stop-MetaMcpAndWait([int]$seconds) {
    $processes = @(Get-HostProcesses)
    if ($processes.Count -ne 0) {
        Write-Host "Stopping bootstrapper PID(s): $($processes.ProcessId -join ', ')."
    }
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $remaining = @(Get-HostProcesses)
        if ($remaining.Count -eq 0) {
            Write-Host 'Previous bootstrapper processes have exited.'
            return
        }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Bootstrapper still running after $seconds seconds: $($remaining.ProcessId -join ', ')."
}

function Wait-PortsFree([int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 12008,12009 -ErrorAction SilentlyContinue)
        if ($listeners.Count -eq 0) { return }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)

    $ports = $listeners | ForEach-Object { "$($_.LocalPort):PID$($_.OwningProcess)" }
    throw "Gateway ports are still occupied: $($ports -join ', ')."
}

function Copy-WhenUnlocked([string]$source, [string]$target, [int]$seconds = 35) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        try {
            Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
            return
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 400
        }
        catch [System.UnauthorizedAccessException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 400
        }
    } while ($true)
}

function Start-Host {
    Start-Process -FilePath $destination -WorkingDirectory $root `
        -ArgumentList "--base `"$root`"" -PassThru
}

function Wait-Health([System.Diagnostics.Process]$process, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $process.Refresh()
        if ($process.HasExited) { return $false }
        try {
            $b = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:12009/health' -TimeoutSec 2
            $f = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:12008/en' -TimeoutSec 2
            if ($b.StatusCode -eq 200 -and $f.StatusCode -ge 200 -and $f.StatusCode -lt 500) {
                return $true
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Update-LaunchMetadata {
    $current = Get-Content -LiteralPath $currentPath -Raw | ConvertFrom-Json
    $current | Add-Member -Force NoteProperty schemaVersion 2
    $current | Add-Member -Force NoteProperty hostPath $root
    $current | Add-Member -Force NoteProperty hostExecutable $destination
    $current.PSObject.Properties.Remove('activeExecutable')
    $current.PSObject.Properties.Remove('previousExecutable')
    $tmp = "$currentPath.tmp"
    [IO.File]::WriteAllText($tmp, ($current | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmp -Destination $currentPath -Force

    $ws = New-Object -ComObject WScript.Shell
    foreach ($shortcut in @(
        (Join-Path $root 'MetaMCP.lnk'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MetaMCP.lnk')
    )) {
        if (-not (Test-Path -LiteralPath $shortcut)) { continue }
        $link = $ws.CreateShortcut($shortcut)
        $link.TargetPath = $destination
        $link.Arguments = "--base `"$root`""
        $link.WorkingDirectory = $root
        $link.IconLocation = "$destination,0"
        $link.Save()
    }
}

$bootstrapper = $null
$completed = $false
if ($originalExists) {
    Copy-WhenUnlocked $destination $backup 15
    Write-Host "Rollback backup saved: $backup"
}

try {
    Stop-MetaMcpAndWait 25
    Wait-PortsFree 25
    Copy-WhenUnlocked $StagedExe $destination 45

    $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "Installed executable SHA256 does not match the staged executable."
    }
    Write-Host 'New bootstrapper copied and SHA256 verified.'

    $bootstrapper = Start-Host
    if (-not (Wait-Health $bootstrapper $HealthTimeoutSeconds)) {
        throw "New bootstrapper PID $($bootstrapper.Id) did not pass backend/frontend health checks."
    }

    Update-LaunchMetadata
    $completed = $true
    Write-Host "Stable bootstrapper healthy: PID=$($bootstrapper.Id) EXE=$destination SHA256=$expectedHash"
}
catch {
    $failure = $_
    Write-Warning "Bootstrapper installation failed: $($failure.Exception.Message)"
    try {
        Stop-MetaMcpAndWait 25
        Wait-PortsFree 25
        if ($originalExists -and (Test-Path -LiteralPath $backup)) {
            Copy-WhenUnlocked $backup $destination 45
            Write-Host 'Rollback executable restored.'
        }
        if (Test-Path -LiteralPath $destination) {
            $restored = Start-Host
            if (Wait-Health $restored $HealthTimeoutSeconds) {
                Write-Host "Rollback host healthy: PID=$($restored.Id)."
            }
            else {
                Write-Warning "Rollback host PID $($restored.Id) did not pass health checks."
            }
        }
    }
    catch {
        Write-Warning "Rollback failed: $($_.Exception.Message)"
    }
    throw $failure
}
finally {
    if ($completed) {
        Remove-Item -LiteralPath $StagedExe -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $root '.bootstrap-publish') -Recurse -Force -ErrorAction SilentlyContinue
    }
}
