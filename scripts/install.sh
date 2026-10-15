#!/usr/bin/env bash
# Installs or updates the current checkout. Linux defaults to ~/.local, without sudo.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
for dependency in node dotnet npm; do
    if ! command -v "$dependency" >/dev/null; then
        echo "Falta $dependency en PATH. Consulta los requisitos de instalación en README.md." >&2
        exit 1
    fi
done
cd "$repo"
exec npm run install:cli -- "$@"
