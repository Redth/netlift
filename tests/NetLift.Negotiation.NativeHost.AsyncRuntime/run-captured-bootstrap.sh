#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
OLD_PROJECT="$HOST_DIR/Managed.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj"
NEW_PROJECT="$HOST_DIR/Managed.New/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj"

if [[ -z "${NETLIFT_NATIVEHOST_CAPTURED_BOOTSTRAP_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_CAPTURED_BOOTSTRAP_ARTIFACTS to a session-local artifact directory.\n' >&2
    exit 2
fi
case "$NETLIFT_NATIVEHOST_CAPTURED_BOOTSTRAP_ARTIFACTS" in
    /*) ;;
    *)
        printf 'Artifact root must be an absolute path.\n' >&2
        exit 2
        ;;
esac
case "$NETLIFT_NATIVEHOST_CAPTURED_BOOTSTRAP_ARTIFACTS" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf 'Artifact root must not be inside shared system temp.\n' >&2
        exit 2
        ;;
esac

mkdir -p "$NETLIFT_NATIVEHOST_CAPTURED_BOOTSTRAP_ARTIFACTS"
ARTIFACT_ROOT="$(cd "$NETLIFT_NATIVEHOST_CAPTURED_BOOTSTRAP_ARTIFACTS" && pwd -P)"
case "$ARTIFACT_ROOT" in
    */.copilot/session-state/*/files/*) ;;
    *)
        printf 'Artifact root must be under the Copilot session files directory.\n' >&2
        exit 2
        ;;
esac

export DOTNET_CLI_HOME="$ARTIFACT_ROOT/dotnet-cli-home"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export TMPDIR="$ARTIFACT_ROOT/tmp"
export NUGET_PACKAGES="$ARTIFACT_ROOT/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$ARTIFACT_ROOT/nuget-http-cache"
mkdir -p "$DOTNET_CLI_HOME" "$TMPDIR" "$NUGET_PACKAGES" \
    "$NUGET_HTTP_CACHE_PATH" "$ARTIFACT_ROOT/empty-nuget-source"

if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    printf 'Captured bootstrap integration requires macOS arm64.\n' >&2
    exit 2
fi
if ! command -v dotnet >/dev/null 2>&1 ||
   ! command -v clang >/dev/null 2>&1 ||
   ! command -v clang++ >/dev/null 2>&1; then
    printf 'Required local tools are unavailable: dotnet, clang, and clang++ are required.\n' >&2
    exit 2
fi
if [[ "$(dotnet --version)" != "11.0.100-rc.1.26425.128" ]]; then
    printf 'Expected .NET SDK 11.0.100-rc.1.26425.128.\n' >&2
    exit 2
fi

DOTNET_BASE_PATH="$(dotnet --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
DOTNET_ROOT="${DOTNET_BASE_PATH%%/sdk/*}"
HOSTFXR_VERSION="11.0.0-rc.1.26425.128"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$HOSTFXR_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$DOTNET_ROOT/host/fxr/$HOSTFXR_VERSION/libhostfxr.dylib" ||
      ! -f "$HOST_NATIVE/hostfxr.h" ||
      ! -f "$HOST_NATIVE/coreclr_delegates.h" ||
      ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Matching .NET 11 RC1 hostfxr, headers, and nethost pack are unavailable.\n' >&2
    exit 2
fi

DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
NATIVE_OUTPUT="$ARTIFACT_ROOT/native"
mkdir -p "$NATIVE_OUTPUT"
build_fixture() {
    local project="$1"
    dotnet restore "$project" --source "$ARTIFACT_ROOT/empty-nuget-source" \
        --artifacts-path "$DOTNET_ARTIFACTS" --verbosity quiet
    dotnet build "$project" --configuration Release --no-restore \
        --artifacts-path "$DOTNET_ARTIFACTS" --nologo --verbosity quiet
}

build_fixture "$OLD_PROJECT"
build_fixture "$NEW_PROJECT"

EXECUTABLE="$NATIVE_OUTPUT/captured-bootstrap-runner"
clang -std=c11 -Wall -Wextra -Werror -pthread \
    -DDEBUG=1 \
    -DNLV5_ENABLE_CAPTURED_BYTES_BOOTSTRAP=1 \
    -DNETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1 \
    -DNETLIFT_NATIVEHOST_SOURCE_V2=1 \
    -I "$CORE_DIR" \
    -I "$HOST_DIR" \
    -I "$ROOT/tests/NetLift.Negotiation.NativeHost" \
    -I "$HOST_NATIVE" \
    "$CORE_DIR/coreclr_bootstrap.c" \
    "$CORE_DIR/managed_bridge_v2.c" \
    "$CORE_DIR/managed_app_host_v1.c" \
    "$CORE_DIR/managed_app_endpoint_authority_v1.c" \
    "$CORE_DIR/managed_app_endpoint_transport_v1.c" \
    "$CORE_DIR/async_v2_core.c" \
    "$CORE_DIR/async_v2_lifecycle.c" \
    "$CORE_DIR/async_v2_accessor.c" \
    "$HOST_DIR/native_v2_test_provider.c" \
    "$HOST_DIR/captured_bootstrap_runner.c" \
    -L "$HOST_NATIVE" -lnethost \
    -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
    -ldl \
    -o "$EXECUTABLE"

OLD_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
NEW_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
OLD_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.Old"
NEW_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.New"
OLD_SCHEMA_HASH="d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b"
NEW_SCHEMA_HASH="969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4"

set_components() {
    local stage="$1"
    local root_name="$2"
    local mode="$3"
    COMPONENT_NAMES=()
    COMPONENT_PATHS=()
    for name in NetLift.Contracts NetLift.Negotiation NetLift.ManagedHost Orders.AsyncBusiness; do
        if [[ "$mode" == missing-dependency && "$name" == NetLift.Contracts ]]; then
            continue
        fi
        local path="$stage/$name.dll"
        if [[ -f "$path" ]]; then
            COMPONENT_NAMES+=("$name")
            COMPONENT_PATHS+=("$path")
        fi
    done
    for path in "$stage"/*.dll; do
        [[ -f "$path" ]] || continue
        local basename="${path##*/}"
        local name="${basename%.dll}"
        if [[ "$mode" == missing-dependency && "$name" == NetLift.Contracts ]]; then
            continue
        fi
        if [[ "$basename" == "$root_name.dll" ]]; then
            continue
        fi
        local already_added=false
        for prior_name in "${COMPONENT_NAMES[@]}"; do
            if [[ "$prior_name" == "$name" ]]; then
                already_added=true
                break
            fi
        done
        if [[ "$already_added" != true ]]; then
            COMPONENT_NAMES+=("$name")
            COMPONENT_PATHS+=("$path")
        fi
    done
    COMPONENT_NAMES+=("$root_name")
    COMPONENT_PATHS+=("$stage/$root_name.dll")
}

replace_staged_assemblies() {
    local stage="$1"
    local case_label="$2"
    for path in "$stage"/*.dll; do
        [[ -f "$path" ]] || continue
        local replacement="$path.replacement"
        printf 'not a managed assembly: %s %s\n' "$case_label" "${path##*/}" >"$replacement"
        local expected
        expected="$(shasum -a 256 "$replacement" | awk '{ print $1 }')"
        mv "$replacement" "$path"
        local actual
        actual="$(shasum -a 256 "$path" | awk '{ print $1 }')"
        if [[ "$actual" != "$expected" ]]; then
            printf 'Staged replacement hash mismatch: %s\n' "$path" >&2
            return 1
        fi
        printf 'STAGED_INVALID path=%s sha256=%s\n' "$path" "$actual"
    done
}

run_case() {
    local direction="$1"
    local mode="$2"
    local assembly namespace schema_hash output
    if [[ "$direction" == old ]]; then
        assembly="$OLD_ASSEMBLY"
        namespace="$OLD_NAMESPACE"
        schema_hash="$OLD_SCHEMA_HASH"
    else
        assembly="$NEW_ASSEMBLY"
        namespace="$NEW_NAMESPACE"
        schema_hash="$NEW_SCHEMA_HASH"
    fi

    output="$DOTNET_ARTIFACTS/bin/$assembly/release"
    local stage="$ARTIFACT_ROOT/staged/$direction-$mode"
    local ready="$ARTIFACT_ROOT/$direction-$mode.ready"
    local go="$ARTIFACT_ROOT/$direction-$mode.go"
    local log="$ARTIFACT_ROOT/$direction-$mode.log"
    mkdir -p "$stage"
    cp -R "$output/." "$stage/"
    if [[ ! -f "$stage/$assembly.dll" ||
          ! -f "$stage/$assembly.runtimeconfig.json" ||
          ! -f "$stage/$assembly.deps.json" ]]; then
        printf 'Required isolated fixture outputs are missing under %s\n' "$output" >&2
        return 1
    fi
    if [[ "$mode" == malformed-root ]]; then
        printf 'not a managed assembly: deliberate malformed captured root\n' \
            >"$stage/$assembly.dll"
    fi

    set_components "$stage" "$assembly" "$mode"
    local component_count="${#COMPONENT_NAMES[@]}"
    local args=(
        "$mode"
        "$DOTNET_ROOT"
        "$stage/$assembly.runtimeconfig.json"
        "$assembly"
        "$namespace"
        "$schema_hash"
        "$ready"
        "$go"
        "$component_count"
    )
    for index in "${!COMPONENT_NAMES[@]}"; do
        local component_hash
        component_hash="$(shasum -a 256 "${COMPONENT_PATHS[$index]}" | awk '{ print $1 }')"
        args+=("${COMPONENT_NAMES[$index]}" "${COMPONENT_PATHS[$index]}" "$component_hash")
    done

    rm -f "$ready" "$go"
    "$EXECUTABLE" "${args[@]}" >"$log" 2>&1 &
    local probe_pid=$!
    local ready_seen=false
    for _ in $(seq 1 3000); do
        if [[ -e "$ready" ]]; then
            ready_seen=true
            break
        fi
        if ! kill -0 "$probe_pid" 2>/dev/null; then
            wait "$probe_pid" || true
            cat "$log"
            printf 'Captured bootstrap runner exited before mutation barrier.\n' >&2
            return 1
        fi
        sleep 0.01
    done
    if [[ "$ready_seen" != true ]]; then
        kill "$probe_pid"
        wait "$probe_pid" || true
        cat "$log"
        printf 'Timed out waiting for captured component snapshot.\n' >&2
        return 1
    fi

    replace_staged_assemblies "$stage" "$direction-$mode"
    touch "$go"
    if ! wait "$probe_pid"; then
        cat "$log"
        return 1
    fi
    cat "$log"

    if [[ "$mode" == positive ]]; then
        grep -Fq "PASS captured NativeApi.GetManifest and complete NativeApi/AsyncRuntimeApi exports" "$log"
        grep -Fq "PASS captured-byte mode never falls back to path bootstrap" "$log"
    elif [[ "$mode" == missing-dependency ]]; then
        grep -Fq "NEGATIVE missing_dependency=component_selection_rejected state=cold" "$log"
    else
        grep -Fq "PASS failed captured-byte mode remains quarantined without path fallback" "$log"
    fi
}

for direction in old new; do
    run_case "$direction" positive
    run_case "$direction" missing-dependency
    run_case "$direction" malformed-root
done

printf 'PASS captured-byte bootstrap OLD/NEW integration; artifacts: %s\n' "$ARTIFACT_ROOT"
