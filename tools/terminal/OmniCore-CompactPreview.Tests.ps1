$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('omni-terminal-density-' + [Guid]::NewGuid().ToString('N'))
$script = Join-Path $PSScriptRoot 'OmniCore-CompactPreview.ps1'
$profile = Join-Path $root 'OmniCoreCompactPreview/fragment.json'
New-Item -ItemType Directory -Path $root | Out-Null
$sentinel = Join-Path $root 'other-profile.json'
Set-Content -LiteralPath $sentinel -Encoding UTF8 -Value 'original'
& $script -FragmentsDirectory $root | Out-Null
if (Test-Path -LiteralPath $profile) { throw 'Mock wrote a profile without Apply' }
& $script -Apply -FragmentsDirectory $root | Out-Null
$json = Get-Content -LiteralPath $profile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($json.profiles[0].antialiasingMode -ne 'cleartype') { throw 'Missing ClearType' }
if ($json.profiles[0].font.face -ne 'Cascadia Mono' -or $json.profiles[0].font.weight -ne 600) { throw 'Missing Cascadia Mono SemiBold' }
if (!$json.profiles[0].commandline.Contains('OMNICORE_TUI_SIDEBAR_DENSITY')) { throw 'Missing temporary density override' }
$original = Get-Content -LiteralPath $profile -Raw -Encoding UTF8
Add-Content -LiteralPath $profile -Value 'user modification'
$refused = $false
try { & $script -Restore -FragmentsDirectory $root | Out-Null } catch { $refused = $true }
if (!$refused -or !(Test-Path -LiteralPath $profile)) { throw 'Restore overwrote a modified user profile' }
Set-Content -LiteralPath $profile -Encoding UTF8 -Value $original
& $script -Restore -FragmentsDirectory $root | Out-Null
if (Test-Path -LiteralPath $profile) { throw 'Restore did not remove the exact owned fragment' }
$withoutFont = [regex]::Replace($original, ',\s*"font": \{\s*"face": "Cascadia Mono",\s*"weight": 600\s*\}', '')
Set-Content -LiteralPath $profile -Encoding UTF8 -Value $withoutFont
& $script -Restore -FragmentsDirectory $root | Out-Null
if (Test-Path -LiteralPath $profile) { throw 'Original sidebar profile without font could not be restored' }
$legacy = $withoutFont.Replace('OmniCore · Panel compacto (prueba)', 'OmniCore · Compacto (prueba)').Replace('OMNICORE_TUI_SIDEBAR_DENSITY', 'OMNICORE_TUI_DENSITY')
Set-Content -LiteralPath $profile -Encoding UTF8 -Value $legacy
& $script -Restore -FragmentsDirectory $root | Out-Null
if (Test-Path -LiteralPath $profile) { throw 'Exact original preview fragment could not be restored' }
if ((Get-Content -LiteralPath $sentinel -Raw -Encoding UTF8).Trim() -ne 'original') { throw 'Unrelated profile changed' }
Write-Output 'PASS: ClearType + Cascadia Mono SemiBold; mock writes nothing; modified profile preserved; current, fontless and legacy restore; unrelated settings intact.'
