[CmdletBinding()]
param(
    [ValidateSet('win-x64','linux-x64','linux-arm64','all')]
    [string]$Target = 'win-x64',
    [string]$Repository = 'C:\DEV\LLM\metamcp'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Build-WindowsCandidate {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
        (Join-Path $PSScriptRoot 'Build-WindowsCandidate.ps1') -Repository $Repository
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Build-LinuxTarget([string]$target) {
    $output = Join-Path $root "Release\$target"
    Push-Location $root
    try {
        & dotnet run --project (Join-Path $root 'src\MetaMCP.Packager') -c Release -- `
            --repo $Repository --target $target --output $output
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally {
        Pop-Location
    }
}

switch ($Target) {
    'win-x64' {
        Build-WindowsCandidate
    }
    'linux-x64' {
        Build-LinuxTarget 'linux-x64'
    }
    'linux-arm64' {
        Build-LinuxTarget 'linux-arm64'
    }
    'all' {
        # Keep Windows on the A/B deployment path instead of recreating legacy Release\win-x64.
        Build-WindowsCandidate
        Build-LinuxTarget 'linux-x64'
        Build-LinuxTarget 'linux-arm64'
    }
}

exit 0
