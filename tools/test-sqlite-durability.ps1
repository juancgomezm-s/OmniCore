# Recovered intent of the old run-tests.ps1, using the actual xUnit v3 executable.
# Local journals/fixtures only: this does not qualify authenticated providers.
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'tests/OmniCore.Tests/OmniCore.Tests.csproj'
$binary = Join-Path $repoRoot 'tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll'
if (-not $NoBuild) {
    & dotnet build $project --no-restore -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing test binary: $binary" }
& dotnet $binary -class '*SqliteEventStoreDurabilityTests' -noLogo
exit $LASTEXITCODE
