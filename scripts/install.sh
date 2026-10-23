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
if [[ "${1:-}" == '--from-github' ]]; then
    shift
    if ! command -v git >/dev/null; then
        echo 'Falta Git en PATH para actualizar desde GitHub.' >&2
        exit 1
    fi
    node tools/npm/sync-main.js
fi
exec npm run install:cli -- "$@"
