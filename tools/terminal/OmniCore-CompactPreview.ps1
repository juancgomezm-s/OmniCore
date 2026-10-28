param(
    [switch]$Apply,
    [switch]$Restore,
    [string]$FragmentsDirectory = (Join-Path $env:LOCALAPPDATA 'Microsoft/Windows Terminal/Fragments')
)

$ErrorActionPreference = 'Stop'
if ($Apply -and $Restore) { throw 'Usa -Apply o -Restore, no ambos.' }
$fragment = @'
{
  "profiles": [
    {
      "guid": "{594a5b40-c298-4b19-af04-b0fc87897029}",
      "name": "OmniCore · Panel compacto (prueba)",
      "commandline": "powershell.exe -NoLogo -NoProfile -NoExit -Command \"$env:OMNICORE_TUI_SIDEBAR_DENSITY='compact'; omni\"",
      "antialiasingMode": "cleartype",
      "font": {
        "face": "Cascadia Mono",
        "weight": 600
      }
    }
  ]
}
'@
$withoutFont = [regex]::Replace($fragment, ',\s*"font": \{\s*"face": "Cascadia Mono",\s*"weight": 600\s*\}', '')
$legacyFragment = $withoutFont.Replace('OmniCore · Panel compacto (prueba)', 'OmniCore · Compacto (prueba)').Replace('OMNICORE_TUI_SIDEBAR_DENSITY', 'OMNICORE_TUI_DENSITY')
$folder = Join-Path ([IO.Path]::GetFullPath($FragmentsDirectory)) 'OmniCoreCompactPreview'
$file = Join-Path $folder 'fragment.json'
if (!$Apply -and !$Restore) {
    Write-Output 'Mock del perfil opcional. No se ha modificado Windows Terminal.'
    Write-Output $fragment
    Write-Output "Destino con -Apply: $file"
    return
}
if (Test-Path -LiteralPath $file) {
    $existing = (Get-Content -LiteralPath $file -Raw -Encoding UTF8).Trim()
    if ($existing -ne $fragment.Trim() -and $existing -ne $withoutFont.Trim() -and $existing -ne $legacyFragment.Trim()) {
        throw 'Ese perfil tiene cambios distintos a este mock; se conserva sin sobrescribirlo.'
    }
}
if ($Restore) {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    Write-Output 'Perfil de prueba retirado. Abre otra ventana para usar tu perfil habitual.'
    return
}
New-Item -ItemType Directory -Path $folder -Force | Out-Null
[IO.File]::WriteAllText($file, $fragment, [Text.UTF8Encoding]::new($false))
Write-Output 'Perfil de prueba añadido. Abre una nueva instancia de Windows Terminal y elige OmniCore · Panel compacto (prueba).'
Write-Output 'Para retirarlo: vuelve a ejecutar este script con -Restore.'
