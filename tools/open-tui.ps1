param([switch]$InWindow, [string]$Workspace)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
if (-not $Workspace) { $Workspace = $repoPath }
$Workspace = (Resolve-Path -LiteralPath $Workspace).Path
$tuiPath = Join-Path $repoPath 'src\OmniCore.Cli\bin\Debug\net10.0\omni.dll'
if (-not (Test-Path -LiteralPath $tuiPath)) {
    throw 'Compile primero: dotnet build src/OmniCore.Cli'
}
if (-not $InWindow) {
    $terminalPath = (Get-Command wt.exe -ErrorAction Stop).Source
    Start-Process -FilePath $terminalPath -ArgumentList (
        '-w new new-tab --title "OmniCore - TUI" --startingDirectory "{0}" powershell.exe -NoProfile -NoExit -File "{1}" -InWindow -Workspace "{0}"' -f $Workspace, $PSCommandPath
    )
    return
}

# Color/encoding overrides belong only to this interactive child process.
$previousNoColor = [Environment]::GetEnvironmentVariable('NO_COLOR', 'Process')
$previousTerm = $env:TERM
$previousColorTerm = $env:COLORTERM
$previousOutput = [Console]::OutputEncoding
$previousInput = [Console]::InputEncoding
try {
    Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
    $env:TERM = 'xterm-256color'
    $env:COLORTERM = 'truecolor'
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
    Set-Location -LiteralPath $Workspace
    & dotnet $tuiPath tui
} finally {
    [Environment]::SetEnvironmentVariable('NO_COLOR', $previousNoColor, 'Process')
    $env:TERM = $previousTerm
    $env:COLORTERM = $previousColorTerm
    [Console]::OutputEncoding = $previousOutput
    [Console]::InputEncoding = $previousInput
}
