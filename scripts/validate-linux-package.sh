#!/usr/bin/env bash
# Requires the SDK from global.json, clang/build-essential/zlib1g-dev and Python 3.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
evidence=${1:?Pass a fresh evidence directory outside the repository}
mkdir -p "$evidence"
evidence=$(cd "$evidence" && pwd)
case "$evidence/" in "$repo/"*) echo 'Evidence must be outside the checkout' >&2; exit 2;; esac
cd "$repo"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for kind in self-contained aot; do
    options=(-p:PublishSingleFile=true)
    if [[ "$kind" == aot ]]; then options=(-p:PublishAot=true); fi
    dotnet publish src/OmniCore.Cli/OmniCore.Cli.csproj -c Release -r linux-x64 \
        --self-contained true "${options[@]}" -p:ILLinkTreatWarningsAsErrors=true \
        -o "$evidence/$kind" > "$evidence/$kind-publish.log" 2>&1
    # A successful linker is insufficient: the published executable must load its native
    # SQLite dependency, parse YAML, persist/replay and verify the real journal.
    python3 scripts/smoke-distribution.py "$evidence/$kind/omni" "$evidence/$kind-smoke" \
        > "$evidence/$kind-smoke.log" 2>&1
    find "$evidence/$kind" -maxdepth 1 -type f -printf '%f %s bytes\n' \
        > "$evidence/$kind-files.txt"
    sha256sum "$evidence/$kind/omni" > "$evidence/$kind.sha256"
    tail -n 3 "$evidence/$kind-publish.log"
    cat "$evidence/$kind-smoke.log"
done
