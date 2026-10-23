param([ValidateSet('Probe', 'Restore')][string]$Action = 'Probe')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This experiment is restricted to the disposable CI account. It must never enable
# LowBox consoles in a developer's/user's Windows profile or silently alter product setup.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or -not $env:RUNNER_TEMP) {
    throw 'ConPTY prerequisite probing is restricted to an ephemeral Windows GitHub runner.'
}
$snapshotPath = Join-Path $env:RUNNER_TEMP 'omnicore-console-prerequisite.clixml'
$registryPath = 'Console'
$valueName = 'LowBoxConsoleEnabled'

function Restore-ConsoleSnapshot {
    if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) { return }
    $snapshot = Import-Clixml -LiteralPath $snapshotPath
    $consoleKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath, $true)
    try {
        if ($snapshot.ValueExisted) {
            if ($null -eq $consoleKey) { $consoleKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPath) }
            $kind = [Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind], $snapshot.Kind))
            $consoleKey.SetValue($valueName, $snapshot.Value, $kind)
        } elseif ($null -ne $consoleKey) {
            $consoleKey.DeleteValue($valueName, $false)
        }
        $removeEmptyKey = -not $snapshot.KeyExisted -and $null -ne $consoleKey `
            -and $consoleKey.ValueCount -eq 0 -and $consoleKey.SubKeyCount -eq 0
    } finally { if ($null -ne $consoleKey) { $consoleKey.Dispose() } }
    if ($removeEmptyKey) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($registryPath, $false) }
    Write-Output 'Original CI console prerequisite restored; diagnostic snapshot retained.'
}

if ($Action -eq 'Restore') { Restore-ConsoleSnapshot; exit 0 }
if (Test-Path -LiteralPath $snapshotPath) { throw 'Original CI console prerequisite was already saved.' }
$module = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll'))
if (-not (Test-Path -LiteralPath $module -PathType Leaf)) { throw 'Build the real test module before probing ConPTY.' }
$evidence = Join-Path $env:RUNNER_TEMP 'omnicore-conpty-prerequisite'
[void][IO.Directory]::CreateDirectory($evidence)

function Invoke-TerminalCase([string]$Name) {
    $log = Join-Path $evidence ($Name + '.log')
    $previousNativePreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        & dotnet $module -method '*WindowsAppContainerSandboxTests.Strong_terminal_uses_ConPTY_for_real_duplex_input_resize_and_output' -parallelMode none *> $log
        $code = $LASTEXITCODE
    } finally { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
    $summary = Get-Content -LiteralPath $log | Where-Object { $_ -match 'Total:\s+\d+.*Errors:' } | Select-Object -Last 1
    $passed = $code -eq 0 -and $null -ne $summary `
        -and $summary -match 'Total:\s+1,\s+Errors:\s+0,\s+Failed:\s+0,\s+Skipped:\s+0,\s+Not Run:\s+0'
    Write-Output ([pscustomobject]@{ Passed = $passed; ExitCode = $code; Summary = $summary; Log = $log })
}

$baseline = Invoke-TerminalCase 'baseline'
Write-Output "ConPTY baseline: passed=$($baseline.Passed); $($baseline.Summary)"
if ($baseline.Passed) { exit 0 }

$consoleKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath)
try {
    $exists = $null -ne $consoleKey -and $consoleKey.GetValueNames() -contains $valueName
    [pscustomobject]@{
        KeyExisted = $null -ne $consoleKey
        ValueExisted = $exists
        Kind = if ($exists) { $consoleKey.GetValueKind($valueName).ToString() } else { $null }
        Value = if ($exists) { $consoleKey.GetValue($valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null }
    } | Export-Clixml -LiteralPath $snapshotPath
} finally { if ($null -ne $consoleKey) { $consoleKey.Dispose() } }

try {
    $consoleKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPath)
    try { $consoleKey.SetValue($valueName, 1, [Microsoft.Win32.RegistryValueKind]::DWord) }
    finally { $consoleKey.Dispose() }
    $configured = Invoke-TerminalCase 'lowbox-enabled'
    Write-Output "ConPTY with LowBox prerequisite: passed=$($configured.Passed); $($configured.Summary)"
    if (-not $configured.Passed) {
        throw "Real ConPTY failed both probes. Evidence: $evidence"
    }
    # Keep this explicit prerequisite for the following full test run in this disposable
    # account. The independent always() workflow step restores the exact original value.
    Write-Output "ConPTY real test passed under the CI-only prerequisite. Evidence: $evidence"
} catch {
    Restore-ConsoleSnapshot
    throw
}
