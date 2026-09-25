[CmdletBinding()]
param(
    [string]$Repository = 'C:\DEV\LLM\metamcp',
    [int]$SwitchDelaySeconds = 12,
    [switch]$InstallDependencies,
    [switch]$BuildOnly,
    [switch]$SwitchOnly
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = Join-Path $PSScriptRoot 'Build-WindowsCandidate.ps1'
$switch = Join-Path $PSScriptRoot 'Switch-WindowsSlot.ps1'
$work = Join-Path $root 'work'
New-Item $work -ItemType Directory -Force | Out-Null

if ($BuildOnly -and $SwitchOnly) {
    throw '-BuildOnly and -SwitchOnly cannot be used together.'
}

if (-not $SwitchOnly) {
    $buildArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$build,'-Repository',$Repository)
    if ($InstallDependencies) { $buildArgs += '-InstallDependencies' }
    & powershell.exe @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Candidate build failed with exit code $LASTEXITCODE." }

    if ($BuildOnly) {
        Write-Host 'Candidate built; switch was not scheduled.'
        exit 0
    }
} elseif (-not (Test-Path (Join-Path $root 'pending-update.json'))) {
    throw 'SwitchOnly requested, but pending-update.json does not exist.'
}

$delay = [Math]::Max(3, $SwitchDelaySeconds)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$cmdPath = Join-Path $work "apply-windows-ab-$stamp.cmd"
$stdout = Join-Path $work "apply-windows-ab-$stamp.out.log"
$stderr = Join-Path $work "apply-windows-ab-$stamp.err.log"

$cmd = @"
@echo off
timeout /t $delay /nobreak >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$switch" 1>>"$stdout" 2>>"$stderr"
set RC=%ERRORLEVEL%
echo %DATE% %TIME% exit=%RC%>>"$stdout"
exit /b %RC%
"@
[IO.File]::WriteAllText($cmdPath, $cmd, [Text.ASCIIEncoding]::new())

# A detached cmd.exe owns the delayed cutover, so killing the live MetaMCP host
# cannot kill the updater that is replacing it.
Start-Process -FilePath $cmdPath -WindowStyle Hidden

Write-Host 'Safe A/B switch scheduled through detached cmd timeout.'
Write-Host "Command file: $cmdPath"
Write-Host "Switch log: $stdout"
