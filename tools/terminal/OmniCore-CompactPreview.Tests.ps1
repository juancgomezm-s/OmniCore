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
if ($json.profiles[0].font.face -ne 'Consolas' -or $json.profiles[0].font.size -ne 12 -or $json.profiles[0].font.weight -ne 400) { throw 'Missing Consolas Regular 12 pt (16 px at 96 DPI)' }
if (!$json.profiles[0].commandline.Contains('OMNICORE_TUI_SIDEBAR_DENSITY')) { throw 'Missing temporary density override' }
$original = Get-Content -LiteralPath $profile -Raw -Encoding UTF8
Add-Content -LiteralPath $profile -Value 'user modification'
$refused = $false
try { & $script -Restore -FragmentsDirectory $root | Out-Null } catch { $refused = $true }
if (!$refused -or !(Test-Path -LiteralPath $profile)) { throw 'Restore overwrote a modified user profile' }
Set-Content -LiteralPath $profile -Encoding UTF8 -Value $original
& $script -Restore -FragmentsDirectory $root | Out-Null
if (Test-Path -LiteralPath $profile) { throw 'Restore did not remove the exact owned fragment' }
$cascadia = [regex]::Replace($original.Replace('"face": "Consolas"', '"face": "Cascadia Mono"').Replace('"weight": 400', '"weight": 600'), '[ \t]*"size": 12,\r?\n', '')
Set-Content -LiteralPath $profile -Encoding UTF8 -Value $cascadia
& $script -Apply -FragmentsDirectory $root | Out-Null
$upgraded = Get-Content -LiteralPath $profile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($upgraded.profiles[0].font.face -ne 'Consolas' -or $upgraded.profiles[0].font.size -ne 12) { throw 'Owned Cascadia preview could not be upgraded' }
& $script -Restore -FragmentsDirectory $root | Out-Null
$withoutFont = [regex]::Replace($original, ',\s*"font": \{\s*"face": "Consolas",\s*"size": 12,\s*"weight": 400\s*\}', '')
Set-Content -LiteralPath $profile -Encoding UTF8 -Value $withoutFont
& $script -Restore -FragmentsDirectory $root | Out-Null
if (Test-Path -LiteralPath $profile) { throw 'Original sidebar profile without font could not be restored' }
$legacy = $withoutFont.Replace('OmniCore · Panel compacto (prueba)', 'OmniCore · Compacto (prueba)').Replace('OMNICORE_TUI_SIDEBAR_DENSITY', 'OMNICORE_TUI_DENSITY')
Set-Content -LiteralPath $profile -Encoding UTF8 -Value $legacy
& $script -Restore -FragmentsDirectory $root | Out-Null
if (Test-Path -LiteralPath $profile) { throw 'Exact original preview fragment could not be restored' }
if ((Get-Content -LiteralPath $sentinel -Raw -Encoding UTF8).Trim() -ne 'original') { throw 'Unrelated profile changed' }
Write-Output 'PASS: ClearType + Consolas 12 pt; mock writes nothing; modified profile preserved; Cascadia upgrade; current, fontless and legacy restore; unrelated settings intact.'
