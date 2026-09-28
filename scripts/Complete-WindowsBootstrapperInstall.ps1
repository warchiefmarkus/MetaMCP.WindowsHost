[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$StagedExe,
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$currentPath = Join-Path $root 'current.json'
$destination = Join-Path $root 'MetaMCP.exe'
$backup = Join-Path $root 'MetaMCP.bootstrap.prev.exe'
$current = Get-Content $currentPath -Raw | ConvertFrom-Json
$previousExe = if ($current.hostExecutable) { [string]$current.hostExecutable } elseif ($current.activeExecutable) { [string]$current.activeExecutable } else { Join-Path ([string]$current.activePath) 'MetaMCP.exe' }

function Stop-MetaMcpTree {
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($_.ExecutablePath) -eq 'MetaMCP.exe'
    })
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Wait-PortsFree([int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        if (@(Get-NetTCPConnection -State Listen -LocalPort 12008,12009 -ErrorAction SilentlyContinue).Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Wait-Health([System.Diagnostics.Process]$process, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        if ($process.HasExited) { return $false }
        try {
            $b = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:12009/health' -TimeoutSec 2
            $f = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:12008/en' -TimeoutSec 2
            if ($b.StatusCode -eq 200 -and $f.StatusCode -ge 200 -and $f.StatusCode -lt 500) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

Stop-MetaMcpTree
if (-not (Wait-PortsFree 20)) { throw 'Ports 12008/12009 did not become free.' }
Remove-Item $backup -Force -ErrorAction SilentlyContinue
if (Test-Path $destination) {
    Copy-Item $destination $backup -Force
}
Copy-Item $StagedExe $destination -Force
$bootstrapper = Start-Process -FilePath $destination -WorkingDirectory $root -ArgumentList @('--base',$root) -PassThru
if (-not (Wait-Health $bootstrapper $HealthTimeoutSeconds)) {
    Stop-Process -Id $bootstrapper.Id -Force -ErrorAction SilentlyContinue
    if (Test-Path $backup) {
        Copy-Item $backup $destination -Force
        Start-Process -FilePath $destination -WorkingDirectory $root -ArgumentList @('--base',$root) | Out-Null
    } elseif ($previousExe -and (Test-Path $previousExe)) {
        Start-Process -FilePath $previousExe -WorkingDirectory (Split-Path $previousExe -Parent) | Out-Null
    }
    throw 'Stable bootstrapper failed health check; previous host was restarted.'
}

$current = Get-Content $currentPath -Raw | ConvertFrom-Json
$current | Add-Member -Force NoteProperty schemaVersion 2
$current | Add-Member -Force NoteProperty hostPath $root
$current | Add-Member -Force NoteProperty hostExecutable $destination
$current.PSObject.Properties.Remove('activeExecutable')
$current.PSObject.Properties.Remove('previousExecutable')
$tmp = "$currentPath.tmp"
[IO.File]::WriteAllText($tmp, ($current | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Move-Item $tmp $currentPath -Force

$ws = New-Object -ComObject WScript.Shell
foreach ($shortcut in @(
    (Join-Path $root 'MetaMCP.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MetaMCP.lnk')
)) {
    if (-not (Test-Path $shortcut)) { continue }
    $s = $ws.CreateShortcut($shortcut)
    $s.TargetPath = $destination
    $s.Arguments = "--base `"$root`""
    $s.WorkingDirectory = $root
    $s.IconLocation = "$destination,0"
    $s.Save()
}

Remove-Item $StagedExe -Force -ErrorAction SilentlyContinue
Remove-Item $backup -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root '.bootstrap-publish') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Stable bootstrapper healthy: PID=$($bootstrapper.Id) EXE=$destination"
