param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = 'juancgomezm-s/OmniCore'
$assetName = "omnicore-cli-$Version-win-x64.tgz"
$manifestName = 'omnicore-release-manifest.json'
$ghVersion = '2.103.3'
$ghArchiveSha256 = '98d0cfa794721665d7973dd728c78aff7bd836d58f10f6d7a03568eccb0a22e0'
$ghExecutableSha256 = 'd303d8ac3f5e9438bf7c977b64dd6425513eae46218f8881d30694f28c1d0dfd'
$semverPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

if ($Version -cnotmatch $semverPattern -or $Tag -cnotin @("v$Version", $Version)) {
    throw 'Release version or tag is not a matching SemVer version.'
}
if ($CommitSha -cnotmatch '^[0-9a-fA-F]{40}$') {
    throw 'CommitSha must be a full 40-character Git SHA.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $CommitSha.ToLowerInvariant()) {
    throw 'The checked-out source does not match CommitSha.'
}

$sourcePackage = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
if ($sourcePackage.name -cne 'omnicore-cli') {
    throw 'Unexpected npm package identity.'
}

if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required for release staging.' }
$artifacts = [IO.Path]::GetFullPath($OutputDirectory)
$runnerTemp = (Resolve-Path -LiteralPath $env:RUNNER_TEMP).Path
function Assert-NoReparseAncestor([string]$candidate) {
    for ($current = [IO.Path]::GetFullPath($candidate); $null -ne $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release staging path traverses a reparse point: $current"
            }
        }
    }
}
Assert-NoReparseAncestor $runnerTemp
$resolvedRunnerTemp = (Resolve-Path -LiteralPath $runnerTemp).Path
if (-not [string]::Equals([IO.Path]::GetDirectoryName($artifacts), $resolvedRunnerTemp, [StringComparison]::OrdinalIgnoreCase) -or
    $artifacts -eq $root -or
    $artifacts.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release staging must be a new direct child of RUNNER_TEMP, outside the source checkout.'
}
if (Test-Path -LiteralPath $artifacts) { throw 'Release output directory already exists; use a fresh runner temporary path.' }
Assert-NoReparseAncestor $artifacts
New-Item -ItemType Directory -Path $artifacts | Out-Null
$staging = Join-Path $artifacts 'package'
New-Item -ItemType Directory -Path $staging | Out-Null

$requiredFiles = @(
    'bin/omni.js',
    'bin/update-helper.js',
    'tools/npm/launcher.js',
    'tools/npm/windows-terminal.js',
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md'
)
foreach ($relative in $requiredFiles) {
    $source = Join-Path $root ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required release package file is missing: $relative"
    }
    $target = Join-Path $staging ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}

$dotnetOutput = Join-Path $staging 'dist/npm'
New-Item -ItemType Directory -Path $dotnetOutput -Force | Out-Null

$ghArchive = Join-Path $artifacts "gh_$($ghVersion)_windows_amd64.zip"
$ghLicensePath = Join-Path $artifacts 'gh-LICENSE'
$ghExtract = Join-Path $artifacts 'gh-extracted'
$ghUri = "https://github.com/cli/cli/releases/download/v$ghVersion/gh_$($ghVersion)_windows_amd64.zip"
Invoke-WebRequest -Uri $ghUri -OutFile $ghArchive
$actualArchiveHash = (Get-FileHash -LiteralPath $ghArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualArchiveHash -cne $ghArchiveSha256) { throw 'The pinned GitHub CLI archive digest did not match.' }
Expand-Archive -LiteralPath $ghArchive -DestinationPath $ghExtract -Force
$ghExecutable = Join-Path $ghExtract 'bin/gh.exe'
if (-not (Test-Path -LiteralPath $ghExecutable -PathType Leaf)) {
    throw 'The pinned GitHub CLI archive is missing gh.exe.'
}
$actualExecutableHash = (Get-FileHash -LiteralPath $ghExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualExecutableHash -cne $ghExecutableSha256) { throw 'The pinned GitHub CLI executable digest did not match.' }
Copy-Item -LiteralPath (Join-Path $ghExtract 'LICENSE') -Destination $ghLicensePath
$ghLicenseSha256 = (Get-FileHash -LiteralPath $ghLicensePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ghLicenseSha256 -cne '8999137010582da29456c10e0a628bf3fa7ead05ab3ae5f424ad25cd0dd94574') {
    throw 'The pinned GitHub CLI license digest did not match.'
}

& dotnet publish (Join-Path $root 'src/OmniCore.Cli/OmniCore.Cli.csproj') `
    --configuration Release --framework net10.0 --runtime win-x64 `
    --output $dotnetOutput --property:UseAppHost=false --no-self-contained `
    "--property:Version=$Version" "--property:VersionPrefix=$Version" `
    "--property:InformationalVersion=$Version" --property:IncludeSourceRevisionInInformationalVersion=false
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $dotnetOutput 'omni.dll') -PathType Leaf)) {
    throw 'The framework-dependent Windows x64 CLI publish failed.'
}
$verifierDirectory = Join-Path $dotnetOutput 'verifier'
New-Item -ItemType Directory -Path $verifierDirectory -Force | Out-Null
Copy-Item -LiteralPath $ghExecutable -Destination (Join-Path $verifierDirectory 'gh.exe')
Copy-Item -LiteralPath $ghLicensePath -Destination (Join-Path $verifierDirectory 'LICENSE')

$package = [ordered]@{
    name = 'omnicore-cli'
    version = $Version
    description = $sourcePackage.description
    license = $sourcePackage.license
    engines = $sourcePackage.engines
    bin = $sourcePackage.bin
    files = @('bin/', 'dist/npm/', 'tools/npm/launcher.js', 'tools/npm/windows-terminal.js', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')
}
$packageJson = Join-Path $staging 'package.json'
$package | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $packageJson -Encoding utf8

Push-Location $staging
try {
    $packOutput = & npm pack --ignore-scripts --pack-destination $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'npm pack failed.' }
}
finally {
    Pop-Location
}
$npmArchive = Join-Path $artifacts "omnicore-cli-$Version.tgz"
if (-not (Test-Path -LiteralPath $npmArchive -PathType Leaf)) {
    throw 'npm pack did not create the expected package archive.'
}
$assetPath = Join-Path $artifacts $assetName
Move-Item -LiteralPath $npmArchive -Destination $assetPath

$assetInfo = Get-Item -LiteralPath $assetPath
if ($assetInfo.Length -lt 1 -or $assetInfo.Length -gt 512MB) {
    throw 'The package size is outside the supported release asset limits.'
}
$sha256 = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    repository = $repository
    version = $Version
    tag = $Tag
    commitSha = $CommitSha.ToLowerInvariant()
    assets = @(
        [ordered]@{
            rid = 'win-x64'
            name = $assetName
            size = [long]$assetInfo.Length
            sha256 = $sha256
            requirements = [ordered]@{
                dotnetMin = '10.0'
                dotnetMax = '10.99'
                nodeMin = '20.0'
                nodeMax = $null
                protocolMin = '1.0'
                protocolMax = '1.99'
                configurationMin = '1'
                configurationMax = '1.99'
            }
        }
    )
}
$manifestPath = Join-Path $artifacts $manifestName
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
if ((Get-Item -LiteralPath $manifestPath).Length -gt 64KB) {
    throw 'The release manifest exceeds the updater size limit.'
}

Remove-Item -LiteralPath $ghArchive -Force
Remove-Item -LiteralPath $ghLicensePath -Force

Write-Output "Prepared $assetName ($($assetInfo.Length) bytes, sha256:$sha256)"
Write-Output "Prepared $manifestName for $Tag at $($CommitSha.ToLowerInvariant())"
