[CmdletBinding()]
param(
    [int]$DelaySeconds = 8
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$currentPath = Join-Path $root 'current.json'
if (-not (Test-Path $currentPath)) { throw "Missing current.json: $currentPath" }
$current = Get-Content $currentPath -Raw | ConvertFrom-Json
$activePath = [string]$current.activePath
if ([string]::IsNullOrWhiteSpace($activePath) -or -not (Test-Path $activePath)) {
    throw "Current active runtime path is invalid: $activePath"
}

foreach ($name in 'config','data') {
    $source = Join-Path $activePath $name
    $target = Join-Path $root $name
    if (-not (Test-Path $target)) {
        New-Item $target -ItemType Directory -Force | Out-Null
        if (Test-Path $source) {
            Copy-Item (Join-Path $source '*') $target -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$publish = Join-Path $root '.bootstrap-publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& dotnet publish (Join-Path $root 'src\MetaMCP.Host.Windows\MetaMCP.Host.Windows.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper publish failed: $LASTEXITCODE" }

$publishedExe = Join-Path $publish 'MetaMCP.exe'
if (-not (Test-Path $publishedExe)) { throw "Published bootstrapper missing: $publishedExe" }
$stagedExe = Join-Path $root 'MetaMCP.bootstrap.next.exe'
Copy-Item $publishedExe $stagedExe -Force

$complete = Join-Path $PSScriptRoot 'Complete-WindowsBootstrapperInstall.ps1'
$work = Join-Path $root 'work'
New-Item $work -ItemType Directory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$cmd = Join-Path $work "install-bootstrapper-$stamp.cmd"
$out = Join-Path $work "install-bootstrapper-$stamp.out.log"
$err = Join-Path $work "install-bootstrapper-$stamp.err.log"
$delay = [Math]::Max(3, $DelaySeconds)
$body = @"
@echo off
timeout /t $delay /nobreak >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$complete" -StagedExe "$stagedExe" 1>>"$out" 2>>"$err"
set RC=%ERRORLEVEL%
echo %DATE% %TIME% exit=%RC%>>"$out"
exit /b %RC%
"@
[IO.File]::WriteAllText($cmd, $body, [Text.ASCIIEncoding]::new())
Start-Process -FilePath $cmd -WindowStyle Hidden
Write-Host "Bootstrapper install scheduled: $cmd"
Write-Host "Staged executable: $stagedExe"
