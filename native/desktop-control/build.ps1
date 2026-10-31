param([string]$Cargo = 'cargo')
$ErrorActionPreference = 'Stop'
$manifest = Join-Path $PSScriptRoot 'Cargo.toml'
& $Cargo test --locked --manifest-path (Join-Path $PSScriptRoot "../desktop-engine/Cargo.toml") --target-dir (Join-Path $PSScriptRoot "target")
if ($LASTEXITCODE -ne 0) { throw "Shared Rust engine tests failed." }
& $Cargo test --locked --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed.' }
& $Cargo build --release --locked --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw 'Rust build failed.' }
$executable = Join-Path $PSScriptRoot 'target/release/omnicore-desktop.exe'
$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'desktop.sha256'), $hash, [Text.UTF8Encoding]::new($false))
$metadataText = & $Cargo metadata --locked --format-version 1 --manifest-path $manifest
if ($LASTEXITCODE -ne 0) { throw 'Cannot collect dependency notices.' }
$metadata = $metadataText | ConvertFrom-Json
$notices = Join-Path $PSScriptRoot 'licenses'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$dependencies = @($metadata.packages | Where-Object { $_.source } | ForEach-Object {
    $package = $_
    $destination = Join-Path $notices ($package.name + '-' + $package.version)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Split-Path $package.manifest_path) -File |
        Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|UNICODE_LICENSE)' } |
        Copy-Item -Destination $destination
    [ordered]@{name=$package.name; version=$package.version; license=$package.license; source=$package.source}
})
[IO.File]::WriteAllText((Join-Path $notices 'dependencies.json'), ($dependencies | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output "Native desktop built and pinned: $hash"
