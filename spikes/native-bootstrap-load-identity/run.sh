#!/usr/bin/env bash
set -euo pipefail

SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SOURCE_DIR/ManagedProbe"
if [[ -z "${NETLIFT_BOOTSTRAP_IDENTITY_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_BOOTSTRAP_IDENTITY_ARTIFACTS to a session-local artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_BOOTSTRAP_IDENTITY_ARTIFACTS" && cd "$NETLIFT_BOOTSTRAP_IDENTITY_ARTIFACTS" && pwd)"
DOTNET_ROOT="$(dotnet --info | awk -F': ' '/Base Path/ { sub(/^[[:space:]]+/, "", $2); sub("/sdk/.*", "", $2); print $2; exit }')"
HOSTFXR_VERSION="11.0.0-rc.1.26425.128"
HOSTFXR="$DOTNET_ROOT/host/fxr/$HOSTFXR_VERSION/libhostfxr.dylib"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$HOSTFXR_VERSION/runtimes/osx-arm64/native"

if [[ ! -f "$HOSTFXR" || ! -f "$HOST_NATIVE/hostfxr.h" ]]; then
    printf 'The expected .NET 11 RC1 hostfxr/header pack is unavailable under %s\n' "$DOTNET_ROOT" >&2
    exit 2
fi

dotnet build "$PROJECT_DIR/ManagedProbe.csproj" -c Release \
    --nologo --artifacts-path "$ARTIFACT_ROOT/build-old" \
    -p:ProbeVariant=old
dotnet build "$PROJECT_DIR/ManagedProbe.csproj" -c Release \
    --nologo --artifacts-path "$ARTIFACT_ROOT/build-new" \
    -p:ProbeVariant=new

OLD_DLL="$ARTIFACT_ROOT/build-old/bin/ManagedProbe/release/ManagedProbe.dll"
NEW_DLL="$ARTIFACT_ROOT/build-new/bin/ManagedProbe/release/ManagedProbe.dll"
RUNTIME_CONFIG="$ARTIFACT_ROOT/build-old/bin/ManagedProbe/release/ManagedProbe.runtimeconfig.json"
PROBE="$ARTIFACT_ROOT/loader_probe"

clang -std=c11 -Wall -Wextra -Werror \
    -I "$HOST_NATIVE" "$SOURCE_DIR/loader_probe.c" \
    -o "$PROBE"
clang -dynamiclib -fvisibility=hidden \
    "$SOURCE_DIR/marker_good.c" \
    -Wl,-install_name,@rpath/libnlprobe_marker.dylib \
    -o "$ARTIFACT_ROOT/libnlprobe_good.dylib"
clang -dynamiclib -fvisibility=hidden \
    "$SOURCE_DIR/marker_replacement.c" \
    -Wl,-install_name,@rpath/libnlprobe_marker.dylib \
    -o "$ARTIFACT_ROOT/libnlprobe_replacement.dylib"

GOOD_INSTALL_NAME="$(otool -D "$ARTIFACT_ROOT/libnlprobe_good.dylib" | tail -n 1)"
REPLACEMENT_INSTALL_NAME="$(otool -D "$ARTIFACT_ROOT/libnlprobe_replacement.dylib" | tail -n 1)"
GOOD_EXPORTS="$(nm -gU "$ARTIFACT_ROOT/libnlprobe_good.dylib" | awk '{ print $NF }' | sort)"
REPLACEMENT_EXPORTS="$(nm -gU "$ARTIFACT_ROOT/libnlprobe_replacement.dylib" | awk '{ print $NF }' | sort)"
if [[ "$GOOD_INSTALL_NAME" != "$REPLACEMENT_INSTALL_NAME" ]]; then
    printf 'Marker dylib install names differ: %s / %s\n' \
        "$GOOD_INSTALL_NAME" "$REPLACEMENT_INSTALL_NAME" >&2
    exit 1
fi
if [[ "$GOOD_EXPORTS" != "$REPLACEMENT_EXPORTS" ||
      "$GOOD_EXPORTS" != "_nl_probe_marker" ]]; then
    printf 'Marker dylib export sets differ or are unexpected:\n%s\n%s\n' \
        "$GOOD_EXPORTS" "$REPLACEMENT_EXPORTS" >&2
    exit 1
fi
printf 'marker_install_name=%s marker_exports=%s\n' \
    "$GOOD_INSTALL_NAME" "$GOOD_EXPORTS"

HOSTFXR_HASH="$(shasum -a 256 "$HOSTFXR" | awk '{ print $1 }')"
OLD_DLL_HASH="$(shasum -a 256 "$OLD_DLL" | awk '{ print $1 }')"
NEW_DLL_HASH="$(shasum -a 256 "$NEW_DLL" | awk '{ print $1 }')"
printf 'expected_hostfxr_sha256=%s\n' "$HOSTFXR_HASH"
printf 'expected_managed_old_sha256=%s\n' "$OLD_DLL_HASH"
printf 'expected_managed_replacement_sha256=%s\n' "$NEW_DLL_HASH"

"$PROBE" hostfxr path "$HOSTFXR" "$HOSTFXR_HASH" \
    2>&1 | tee "$ARTIFACT_ROOT/actual-hostfxr-path.log"
"$PROBE" hostfxr fd "$HOSTFXR" "$HOSTFXR_HASH" \
    2>&1 | tee "$ARTIFACT_ROOT/actual-hostfxr-fd.log"
grep -Fq 'hostfxr_symbol=' "$ARTIFACT_ROOT/actual-hostfxr-path.log"
grep -Fq 'hostfxr_symbol=' "$ARTIFACT_ROOT/actual-hostfxr-fd.log"
if grep -Fq 'FD_DLOPEN_UNSUPPORTED' "$ARTIFACT_ROOT/actual-hostfxr-fd.log"; then
    printf 'The actual RC1 hostfxr /dev/fd load was not supported.\n' >&2
    exit 1
fi

run_marker_race() {
    local mode="$1"
    local target="$ARTIFACT_ROOT/marker-target-$mode.dylib"
    local replacement="$ARTIFACT_ROOT/marker-replacement-$mode.dylib"
    local ready="$ARTIFACT_ROOT/marker-$mode.ready"
    local go="$ARTIFACT_ROOT/marker-$mode.go"
    local log="$ARTIFACT_ROOT/marker-$mode.log"
    cp "$ARTIFACT_ROOT/libnlprobe_good.dylib" "$target"
    cp "$ARTIFACT_ROOT/libnlprobe_replacement.dylib" "$replacement"
    rm -f "$ready" "$go"

    "$PROBE" marker-race "$mode" "$target" \
        "$(shasum -a 256 "$target" | awk '{ print $1 }')" "$ready" "$go" \
        >"$log" 2>&1 &
    local probe_pid=$!
    local ready_seen=false
    for _ in $(seq 1 500); do
        if [[ -e "$ready" ]]; then
            ready_seen=true
            break
        fi
        if ! kill -0 "$probe_pid" 2>/dev/null; then
            wait "$probe_pid"
            cat "$log"
            return 1
        fi
        sleep 0.02
    done
    if [[ "$ready_seen" != true ]]; then
        kill "$probe_pid"
        wait "$probe_pid" || true
        cat "$log"
        printf 'Timed out waiting for the native probe handshake.\n' >&2
        return 1
    fi

    mv "$replacement" "$target"
    touch "$go"
    wait "$probe_pid"
    cat "$log"
}

run_marker_race path
run_marker_race fd
grep -Fq 'marker=2222' "$ARTIFACT_ROOT/marker-path.log"
grep -Fq 'marker=1111' "$ARTIFACT_ROOT/marker-fd.log"

MANAGED_TARGET="$ARTIFACT_ROOT/ManagedProbe.dll"
MANAGED_REPLACEMENT="$ARTIFACT_ROOT/ManagedProbe.replacement.dll"

run_managed_probe() {
    local mode="$1"
    local replace="$2"
    local ready="$ARTIFACT_ROOT/managed-$mode.ready"
    local go="$ARTIFACT_ROOT/managed-$mode.go"
    local log="$ARTIFACT_ROOT/managed-$mode.log"
    cp "$OLD_DLL" "$MANAGED_TARGET"
    cp "$NEW_DLL" "$MANAGED_REPLACEMENT"
    rm -f "$ready" "$go"

    "$PROBE" managed-race "$HOSTFXR" "$HOSTFXR_HASH" \
        "$RUNTIME_CONFIG" "$MANAGED_TARGET" "$OLD_DLL_HASH" "$ready" "$go" \
        >"$log" 2>&1 &
    local probe_pid=$!
    local ready_seen=false
    for _ in $(seq 1 500); do
        if [[ -e "$ready" ]]; then
            ready_seen=true
            break
        fi
        if ! kill -0 "$probe_pid" 2>/dev/null; then
            wait "$probe_pid"
            cat "$log"
            return 1
        fi
        sleep 0.02
    done
    if [[ "$ready_seen" != true ]]; then
        kill "$probe_pid"
        wait "$probe_pid" || true
        cat "$log"
        printf 'Timed out waiting for the managed component probe handshake.\n' >&2
        return 1
    fi

    if [[ "$replace" == true ]]; then
        mv "$MANAGED_REPLACEMENT" "$MANAGED_TARGET"
    fi
    touch "$go"
    wait "$probe_pid"
    cat "$log"
}

run_managed_probe control false
grep -Fq 'managed_marker=1111' "$ARTIFACT_ROOT/managed-control.log"
run_managed_probe path-race true
grep -Fq 'managed_marker=2222' "$ARTIFACT_ROOT/managed-path-race.log"

printf 'Artifacts and logs: %s\n' "$ARTIFACT_ROOT"
