param([switch]$Snapshots)

# Real Terminal.Gui driver + temporary journal fixtures. No provider calls or user clipboard access.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('omni-conversation-qa-' + [Guid]::NewGuid().ToString('N'))
$buildDirectory = Join-Path $runDirectory 'bin'
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$previousTerm = $env:TERM
$previousNoColor = $env:NO_COLOR
$previousSnapshots = $env:OMNICORE_TUI_SNAPSHOT_DIR
Push-Location $repoRoot
try {
    $env:TERM = 'xterm-256color'
    Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
    if ($Snapshots) { $env:OMNICORE_TUI_SNAPSHOT_DIR = Join-Path $runDirectory 'frames' }
    else { Remove-Item Env:OMNICORE_TUI_SNAPSHOT_DIR -ErrorAction SilentlyContinue }
    & dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore "-p:OutputPath=$buildDirectory/" -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación; no se ejecutaron las pruebas.' }
    $testLog = Join-Path $runDirectory 'tests.log'
    & dotnet (Join-Path $buildDirectory 'OmniCore.Tests.dll') -class '*TuiWiringTests' -class '*MarkdownTableRendererTests' -class '*ConversationPresentationTests' -class '*ConversationSyntaxHighlighterTests' -noLogo *> $testLog
    $testExit = $LASTEXITCODE
    $report = Get-Content -LiteralPath $testLog -Raw
    foreach ($failure in [regex]::Matches($report, '\[FAIL\]')) {
        $report.Substring($failure.Index, [Math]::Min(1000, $report.Length - $failure.Index)) -replace '\x1b\[[0-9;?]*[A-Za-z]', '' | Write-Host
    }
    [regex]::Matches($report, 'OmniCore.Tests\s+Total:[^\r\n]+') | ForEach-Object { Write-Host $_.Value }
    Write-Host "Evidencia: $runDirectory"
    if ($testExit -ne 0) { throw "Falló la suite (exit $testExit). Se conservaron los logs." }
} finally {
    $env:TERM = $previousTerm
    $env:NO_COLOR = $previousNoColor
    $env:OMNICORE_TUI_SNAPSHOT_DIR = $previousSnapshots
    Pop-Location
}
