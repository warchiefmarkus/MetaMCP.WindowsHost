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

    # A real swap cannot run without a stable Host EXE. Verify that the
    # recovery fails immediately and never manufactures current.json.
    [IO.File]::WriteAllText(
        (Join-Path $config 'host.json'),
        '{"BackendPort":12009,"HostControlToken":"test-token"}',
        [Text.UTF8Encoding]::new($false))
    $ErrorActionPreference = 'Continue'
    $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'Swap-WindowsRuntime.ps1') -Root $temp 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($exitCode -eq 0 -or $output -notmatch 'Stable MetaMCP Host executable not found') {
        throw "Missing Host EXE guard failed: $output"
    }
    if (Test-Path (Join-Path $temp 'current.json')) {
        throw 'Swap incorrectly manufactured current.json.'
    }
    Write-Host 'PASS missing Host EXE refuses swap without creating current.json'
}
finally {
    Remove-Item $temp -Force -Recurse -ErrorAction SilentlyContinue
}