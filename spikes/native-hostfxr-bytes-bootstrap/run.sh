#!/usr/bin/env bash
set -euo pipefail

SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
if [[ -z "${NETLIFT_HOSTFXR_BYTES_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_HOSTFXR_BYTES_ARTIFACTS to a session-local artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_HOSTFXR_BYTES_ARTIFACTS" && cd "$NETLIFT_HOSTFXR_BYTES_ARTIFACTS" && pwd)"
DOTNET_ROOT="$(dotnet --info | awk -F': ' '/Base Path/ { sub(/^[[:space:]]+/, "", $2); sub("/sdk/.*", "", $2); print $2; exit }')"
HOSTFXR_VERSION="11.0.0-rc.1.26425.128"
HOSTFXR="$DOTNET_ROOT/host/fxr/$HOSTFXR_VERSION/libhostfxr.dylib"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$HOSTFXR_VERSION/runtimes/osx-arm64/native"

if [[ ! -f "$HOSTFXR" || ! -f "$HOST_NATIVE/hostfxr.h" ||
      ! -f "$HOST_NATIVE/coreclr_delegates.h" ]]; then
    printf 'Expected .NET 11 RC1 hostfxr/delegate headers are unavailable under %s\n' "$DOTNET_ROOT" >&2
    exit 2
fi

dotnet build "$SOURCE_DIR/ManagedProbe/ManagedProbe.csproj" -c Release \
    --nologo --artifacts-path "$ARTIFACT_ROOT/build-old" \
    -p:ProbeVariant=old
dotnet build "$SOURCE_DIR/ManagedProbe/ManagedProbe.csproj" -c Release \
    --nologo --artifacts-path "$ARTIFACT_ROOT/build-new" \
    -p:ProbeVariant=new
dotnet build "$SOURCE_DIR/IdentityCheck/IdentityCheck.csproj" -c Release \
    --nologo --artifacts-path "$ARTIFACT_ROOT/identity-check"
dotnet build src/NetLift.ManagedHost/NetLift.ManagedHost.csproj -c Release \
    --nologo --artifacts-path "$ARTIFACT_ROOT/managedhost-build"

OLD_OUTPUT="$ARTIFACT_ROOT/build-old/bin/ManagedProbe/release"
NEW_OUTPUT="$ARTIFACT_ROOT/build-new/bin/ManagedProbe/release"
IDENTITY_CHECK="$ARTIFACT_ROOT/identity-check/bin/IdentityCheck/release/IdentityCheck.dll"
RUNTIME_CONFIG="$OLD_OUTPUT/ManagedProbe.runtimeconfig.json"
OLD_DLL="$OLD_OUTPUT/ManagedProbe.dll"
NEW_DLL="$NEW_OUTPUT/ManagedProbe.dll"
MANAGED_HOST_OUTPUT="$ARTIFACT_ROOT/managedhost-build/bin/NetLift.ManagedHost/release"
CONTRACTS_DLL="$ARTIFACT_ROOT/managedhost-build/bin/NetLift.Contracts/release/NetLift.Contracts.dll"
NEGOTIATION_DLL="$ARTIFACT_ROOT/managedhost-build/bin/NetLift.Negotiation/release/NetLift.Negotiation.dll"
MANAGED_HOST_DLL="$MANAGED_HOST_OUTPUT/NetLift.ManagedHost.dll"
PROBE="$ARTIFACT_ROOT/bytes_probe"

dotnet "$IDENTITY_CHECK" "$OLD_DLL" "$NEW_DLL" \
    2>&1 | tee "$ARTIFACT_ROOT/fixture-identity.log"
clang -std=c11 -Wall -Wextra -Werror \
    -I "$HOST_NATIVE" "$SOURCE_DIR/bytes_probe.c" \
    -o "$PROBE"

HOSTFXR_HASH="$(shasum -a 256 "$HOSTFXR" | awk '{ print $1 }')"
OLD_DLL_HASH="$(shasum -a 256 "$OLD_DLL" | awk '{ print $1 }')"
NEW_DLL_HASH="$(shasum -a 256 "$NEW_DLL" | awk '{ print $1 }')"
CONTRACTS_HASH="$(shasum -a 256 "$CONTRACTS_DLL" | awk '{ print $1 }')"
NEGOTIATION_HASH="$(shasum -a 256 "$NEGOTIATION_DLL" | awk '{ print $1 }')"
MANAGED_HOST_HASH="$(shasum -a 256 "$MANAGED_HOST_DLL" | awk '{ print $1 }')"
printf 'expected_hostfxr_sha256=%s\n' "$HOSTFXR_HASH"
printf 'expected_probe_old_sha256=%s\n' "$OLD_DLL_HASH"
printf 'expected_probe_new_sha256=%s\n' "$NEW_DLL_HASH"
printf 'expected_contracts_sha256=%s\n' "$CONTRACTS_HASH"
printf 'expected_negotiation_sha256=%s\n' "$NEGOTIATION_HASH"
printf 'expected_managed_host_sha256=%s\n' "$MANAGED_HOST_HASH"

assert_sha256() {
    local path="$1"
    local expected="$2"
    local actual
    actual="$(shasum -a 256 "$path" | awk '{ print $1 }')"
    if [[ "$actual" != "$expected" ]]; then
        printf 'SHA-256 mismatch for %s: expected=%s actual=%s\n' \
            "$path" "$expected" "$actual" >&2
        return 1
    fi
}

run_fixture_case() {
    local mode="$1"
    local expected_marker="$2"
    local case_dir="$ARTIFACT_ROOT/$mode-case"
    local target="$case_dir/ManagedProbe.dll"
    local replacement="$case_dir/ManagedProbe.replacement.dll"
    local ready="$ARTIFACT_ROOT/$mode.ready"
    local go="$ARTIFACT_ROOT/$mode.go"
    local log="$ARTIFACT_ROOT/$mode.log"

    mkdir -p "$case_dir"
    cp "$OLD_DLL" "$target"
    cp "$RUNTIME_CONFIG" "$case_dir/ManagedProbe.runtimeconfig.json"
    cp "$NEW_DLL" "$replacement"
    assert_sha256 "$target" "$OLD_DLL_HASH"
    assert_sha256 "$replacement" "$NEW_DLL_HASH"
    rm -f "$ready" "$go"
    "$PROBE" "$mode" "$HOSTFXR" "$HOSTFXR_HASH" "$RUNTIME_CONFIG" \
        "$target" "$OLD_DLL_HASH" "$expected_marker" "$ready" "$go" \
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
        printf 'Timed out waiting for %s probe handshake.\n' "$mode" >&2
        return 1
    fi

    mv "$replacement" "$target"
    assert_sha256 "$target" "$NEW_DLL_HASH"
    touch "$go"
    wait "$probe_pid"
    cat "$log"
}

run_fixture_case path 2222
grep -Fq 'fixture_mode=path marker=2222 secondary_marker=2223' \
    "$ARTIFACT_ROOT/path.log"
run_fixture_case bytes 1111
grep -Fq 'fixture_mode=bytes marker=1111 secondary_marker=1112' \
    "$ARTIFACT_ROOT/bytes.log"
grep -Fq 'load_assembly_bytes_status=0' "$ARTIFACT_ROOT/bytes.log"
grep -Fq 'invalid_bytes_status=' "$ARTIFACT_ROOT/bytes.log"
grep -Fq 'missing_entrypoint_status=' "$ARTIFACT_ROOT/bytes.log"

run_managedhost_mutation() {
    local mode="$1"
    local scratch="$ARTIFACT_ROOT/$mode"
    local managed_host="$scratch/NetLift.ManagedHost.dll"
    local replacement="$scratch/NetLift.ManagedHost.replacement"
    local ready="$ARTIFACT_ROOT/$mode.ready"
    local go="$ARTIFACT_ROOT/$mode.go"
    local log="$ARTIFACT_ROOT/$mode.log"

    mkdir -p "$scratch"
    cp "$CONTRACTS_DLL" "$scratch/NetLift.Contracts.dll"
    cp "$NEGOTIATION_DLL" "$scratch/NetLift.Negotiation.dll"
    cp "$MANAGED_HOST_DLL" "$managed_host"
    assert_sha256 "$managed_host" "$MANAGED_HOST_HASH"
    rm -f "$ready" "$go"

    if [[ "$mode" == "managed-host-bytes" ]]; then
        "$PROBE" managed-host "$HOSTFXR" "$HOSTFXR_HASH" "$RUNTIME_CONFIG" \
            "$scratch/NetLift.Contracts.dll" "$CONTRACTS_HASH" \
            "$scratch/NetLift.Negotiation.dll" "$NEGOTIATION_HASH" \
            "$managed_host" "$MANAGED_HOST_HASH" "$ready" "$go" \
            >"$log" 2>&1 &
    else
        "$PROBE" managed-host-path-control "$HOSTFXR" "$HOSTFXR_HASH" \
            "$RUNTIME_CONFIG" "$managed_host" "$MANAGED_HOST_HASH" \
            "$ready" "$go" >"$log" 2>&1 &
    fi
    local probe_pid=$!
    local ready_seen=false
    for _ in $(seq 1 500); do
        if [[ -e "$ready" ]]; then
            ready_seen=true
            break
        fi
        if ! kill -0 "$probe_pid" 2>/dev/null; then
            wait "$probe_pid" || true
            cat "$log"
            return 1
        fi
        sleep 0.02
    done
    if [[ "$ready_seen" != true ]]; then
        kill "$probe_pid"
        wait "$probe_pid" || true
        cat "$log"
        printf 'Timed out waiting for %s verification handshake.\n' "$mode" >&2
        return 1
    fi

    printf 'not a managed assembly: staged replacement for %s\n' "$mode" >"$replacement"
    local replacement_hash
    replacement_hash="$(shasum -a 256 "$replacement" | awk '{ print $1 }')"
    if [[ "$replacement_hash" == "$MANAGED_HOST_HASH" ]]; then
        printf 'Invalid replacement unexpectedly matches ManagedHost hash.\n' >&2
        return 1
    fi
    mv "$replacement" "$managed_host"
    assert_sha256 "$managed_host" "$replacement_hash"
    printf 'managedhost_mutation_case=%s path=%s replacement_sha256=%s payload=invalid-text\n' \
        "$mode" "$managed_host" "$replacement_hash"
    touch "$go"

    if ! wait "$probe_pid"; then
        cat "$log"
        return 1
    fi
    cat "$log"
}

run_managedhost_mutation managed-host-bytes
grep -Fq 'managedhost_replacement_handshake=passed' \
    "$ARTIFACT_ROOT/managed-host-bytes.log"
grep -Fq 'managedhost_export=NetLift.ManagedHost.ManagedAppEndpointAuthorityApiV1Exports, NetLift.ManagedHost status=0 required_size=48' \
    "$ARTIFACT_ROOT/managed-host-bytes.log"
grep -Fq 'managedhost_export=NetLift.ManagedHost.ManagedAppHostApiV1Exports, NetLift.ManagedHost status=0 required_size=24' \
    "$ARTIFACT_ROOT/managed-host-bytes.log"
grep -Fq 'managedhost_export=NetLift.ManagedHost.ManagedAppEndpointTransportApiV1Exports, NetLift.ManagedHost status=0 required_size=64' \
    "$ARTIFACT_ROOT/managed-host-bytes.log"

run_managedhost_mutation managed-host-path-control
grep -Fq 'managedhost_path_control_status=' \
    "$ARTIFACT_ROOT/managed-host-path-control.log"

printf 'Artifacts and logs: %s\n' "$ARTIFACT_ROOT"
