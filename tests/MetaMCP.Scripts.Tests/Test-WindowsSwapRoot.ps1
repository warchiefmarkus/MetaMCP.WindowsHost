[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temp = Join-Path ([IO.Path]::GetTempPath()) ("MetaMCP-SwapRoot-" + [guid]::NewGuid().ToString('N'))
try {
    $scripts = Join-Path $temp 'scripts'
    $config = Join-Path $temp 'config'
    New-Item -ItemType Directory -Path $scripts, $config -Force | Out-Null
    foreach ($name in @('Swap-WindowsRuntime.ps1', 'Update-WindowsAB.ps1')) {
        Copy-Item (Join-Path $repo "scripts\$name") $scripts
    }
    [IO.File]::WriteAllText(
        (Join-Path $config 'host.json'),
        '{"BackendPort":12009,"HostControlToken":""}',
        [Text.UTF8Encoding]::new($false))

    foreach ($test in @(
        @{ Label = 'implicit root'; Name = 'Swap-WindowsRuntime.ps1'; Extra = @('-Status') },
        @{ Label = 'explicit root'; Name = 'Swap-WindowsRuntime.ps1'; Extra = @('-Status','-Root',$temp) },
        @{ Label = 'SwapOnly wrapper'; Name = 'Update-WindowsAB.ps1'; Extra = @('-SwapOnly') }
    )) {
        $script = Join-Path $scripts $test.Name
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$script) + $test.Extra
        $ErrorActionPreference = 'Continue'
        $output = (& powershell.exe @arguments 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($exitCode -eq 0 -or $output -notmatch 'HostControlToken is missing from config') {
            throw "$($test.Label) failed root validation: $output"
        }
        Write-Host "PASS $($test.Label)"
    }
}
finally {
    Remove-Item $temp -Force -Recurse -ErrorAction SilentlyContinue
}