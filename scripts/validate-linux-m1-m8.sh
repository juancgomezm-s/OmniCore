#!/bin/sh
# Real Linux namespaces + isolated Secret Service; no repository or model copies.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
artifacts=${OMNICORE_LINUX_ARTIFACTS_DIR:-"$root/artifacts/linux-m1-m8"}
mkdir -p "$artifacts"
for command in dotnet cc bwrap dbus-run-session dbus-send gnome-keyring-daemon secret-tool python3 node; do
    command -v "$command" >/dev/null || { echo "Missing Linux dependency: $command" >&2; exit 2; }
done
cd "$root"
dotnet restore tests/OmniCore.Tests/OmniCore.Tests.csproj -p:ArtifactsPath="$artifacts/build" >"$artifacts/restore.log" 2>&1
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -p:ArtifactsPath="$artifacts/build" -v:minimal >"$artifacts/build.log" 2>&1
runner="$artifacts/build/bin/OmniCore.Tests/debug/OmniCore.Tests.dll"
test -f "$runner"
export OMNICORE_LINUX_ARTIFACTS_DIR="$artifacts"
if [ "$#" -gt 0 ]; then
    sh "$root/scripts/with-linux-secret-service.sh" dotnet "$runner" "$@" \
        -noLogo -noColor -result-xml "$artifacts/focused-tests.xml"
    exit
fi
sh "$root/scripts/with-linux-secret-service.sh" dotnet "$runner" \
    -class OmniCore.Tests.CredentialKeyMigrationTests -class OmniCore.Tests.LinuxSecretServiceTests \
    -class OmniCore.Tests.LinuxBatchSandboxTests -class OmniCore.Tests.ExtensionIsolationTests \
    -class OmniCore.Tests.ProcessExecToolsTests -class OmniCore.Tests.M2ComponentTests \
    -noLogo -noColor -result-xml "$artifacts/tests.xml"
