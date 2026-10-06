param(
    [string]$OmniCoderRoot = 'C:\Users\juanc\source\repos\OmniCoder',
    [switch]$Accounts,
    [switch]$FullSuite
)
$ErrorActionPreference = 'Stop'
$coreRoot = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path ([IO.Path]::GetTempPath()) ('omni-session-observability-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$previousTerm = $env:TERM
$previousNoColor = $env:NO_COLOR
Push-Location $coreRoot
try {
    # TUI visual assertions need the same declared color terminal as the visual QA runner.
    $env:TERM = 'xterm-256color'
    Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
    & dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj -v quiet *> (Join-Path $evidence 'build-core.log')
    if ($LASTEXITCODE -ne 0) { throw 'Core build failed; see evidence.' }
    $testArgs = @('-noLogo', '-parallel', 'none')
    if (!$FullSuite) { $testArgs += @('-class', '*SessionObservabilityTests', '-class', '*ModelStepEventContractTests', '-class', '*SessionUsageReporterTests', '-class', '*CliEndToEndTests', '-class', '*AnthropicMessagesProviderTests', '-class', '*OpenAIResponsesProviderTests', '-class', '*ContextCheckpointTests') }
    & dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll @testArgs *> (Join-Path $evidence 'fixtures-core.log')
    if ($LASTEXITCODE -ne 0) { throw 'Core fixture tests failed; see evidence.' }
    $properties = @("-p:OmniCoreRoot=$coreRoot", "-p:OmniCoderRoot=$OmniCoderRoot")
    & dotnet build tools/OmniCoderMetricsProbe/OmniCoderMetricsProbe.csproj @properties -v quiet *> (Join-Path $evidence 'build-probe.log')
    if ($LASTEXITCODE -ne 0) { throw 'Integration probe build failed; see evidence.' }
    & dotnet tools/OmniCoderMetricsProbe/bin/Debug/net10.0-windows/OmniCoderMetricsProbe.dll *> (Join-Path $evidence 'fixtures-wpf-integration.log')
    if ($LASTEXITCODE -ne 0) { throw 'WPF integration fixture failed; see evidence.' }
    if ($Accounts) {
        # Official CLI login reuse, read-only quota queries. No inference requests or credentials in logs.
        & dotnet tools/OmniCoderMetricsProbe/bin/Debug/net10.0-windows/OmniCoderMetricsProbe.dll --accounts *> (Join-Path $evidence 'accounts-read-only.log')
        if ($LASTEXITCODE -ne 0) { throw 'Read-only account probe failed; see evidence.' }
    }
    Write-Host 'PASS: fixtures and real cross-project WPF integration. Fixtures do not validate provider consumption.'
} finally {
    $env:TERM = $previousTerm
    $env:NO_COLOR = $previousNoColor
    Write-Host "Evidence: $evidence"
    Pop-Location
}
