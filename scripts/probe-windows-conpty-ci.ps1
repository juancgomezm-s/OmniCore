param([ValidateSet('Probe')][string]$Action = 'Probe')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or -not $env:RUNNER_TEMP) {
    throw 'This fail-fast ConPTY acceptance check runs only on a Windows GitHub runner.'
}
$module = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll'))
if (-not (Test-Path -LiteralPath $module -PathType Leaf)) { throw 'Build the real test module before probing ConPTY.' }
$evidence = Join-Path $env:RUNNER_TEMP 'omnicore-conpty-prerequisite'
[void][IO.Directory]::CreateDirectory($evidence)
$log = Join-Path $evidence 'acceptance.log'
$previousNativePreference = $PSNativeCommandUseErrorActionPreference
try {
    $PSNativeCommandUseErrorActionPreference = $false
    & dotnet $module -method '*WindowsAppContainerSandboxTests.Strong_terminal_uses_ConPTY_for_real_duplex_input_resize_and_output' -parallelMode none *> $log
    $code = $LASTEXITCODE
} finally { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
$summary = Get-Content -LiteralPath $log | Where-Object { $_ -match 'Total:\s+\d+.*Errors:' } | Select-Object -Last 1
$passed = $code -eq 0 -and $null -ne $summary `
    -and $summary -match 'Total:\s+1,\s+Errors:\s+0,\s+Failed:\s+0,\s+Skipped:\s+0,\s+Not Run:\s+0'
Write-Output "ConPTY acceptance: passed=$passed; $summary"
if (-not $passed) {
    Get-Content -LiteralPath $log
    throw 'The real ConPTY test failed. No environment or security settings were changed.'
}
