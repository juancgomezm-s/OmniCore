#!/bin/sh
# Own D-Bus/keyring session only; never unlock or stop a user's existing keyring.
set -eu
test "$#" -gt 0
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
artifacts=${OMNICORE_LINUX_ARTIFACTS_DIR:-"$root/artifacts/linux-secret-service"}
mkdir -p "$artifacts"
profile=$(mktemp -d "$artifacts/keyring-session.XXXXXX")
printf 'Isolated Secret Service profile: %s\n' "$profile"
export XDG_DATA_HOME="$profile/data" XDG_CONFIG_HOME="$profile/config" XDG_CACHE_HOME="$profile/cache"
export OMNICORE_KEYRING_TEST_PROFILE="$profile" OMNICORE_TEST_SECRET_SERVICE=1
mkdir -p "$XDG_DATA_HOME" "$XDG_CONFIG_HOME" "$XDG_CACHE_HOME" "$profile/control"
dbus-run-session -- sh -eu -c '
    printf "%s" "isolated-fixture-keyring-password" | gnome-keyring-daemon --foreground --unlock --components=secrets --control-directory="$OMNICORE_KEYRING_TEST_PROFILE/control" >"$OMNICORE_KEYRING_TEST_PROFILE/daemon.log" 2>&1 &
    keyring_pid=$!
    trap '\''kill -TERM "$keyring_pid" 2>/dev/null || :; wait "$keyring_pid" 2>/dev/null || :'\'' EXIT HUP INT TERM
    ready=false
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        if dbus-send --session --print-reply --reply-timeout=1000 --dest=org.freedesktop.DBus /org/freedesktop/DBus org.freedesktop.DBus.NameHasOwner string:org.freedesktop.secrets 2>/dev/null | grep -q "boolean true"; then ready=true; break; fi
        sleep 0.2
    done
    test "$ready" = true
    "$@"
' isolated-secret-service "$@"
