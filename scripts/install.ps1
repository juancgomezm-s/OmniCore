# Installs or updates the current checkout, without changing user configuration/data.
[CmdletBinding()]
param([string]$Prefix)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Get-Command node -ErrorAction Stop | Out-Null
Get-Command dotnet -ErrorAction Stop | Out-Null
Get-Command npm.cmd -ErrorAction Stop | Out-Null
$installArgs = @('run', 'install:cli', '--')
if ($Prefix) { $installArgs += @('--prefix', $Prefix) }
Push-Location -LiteralPath $repo
try {
    # Windows PowerShell 5.1 treats redirected native stderr (including npm notices)
    # as ErrorRecords. Preserve that output and use the actual process exit status.
    $savedErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & npm.cmd @installArgs
    $installExitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedErrorActionPreference
    if ($installExitCode -ne 0) { exit $installExitCode }
} finally {
    $ErrorActionPreference = 'Stop'
    Pop-Location
}
