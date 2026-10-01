#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPIKE_DIR="$ROOT/spikes/native-captured-registration-seam"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
SMOKE_PROJECT="$ROOT/tests/NetLift.ManagedHost.Smoke/NetLift.ManagedHost.Smoke.csproj"
BRIDGE_PROJECT="$SPIKE_DIR/Bridge/NetLift.NativeCapturedRegistrationSeam.Bridge.csproj"

if [[ -z "${NETLIFT_NATIVE_CAPTURED_REGISTRATION_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVE_CAPTURED_REGISTRATION_ARTIFACTS to an absolute session-files scratch root.\n' >&2
    exit 2
fi
case "$NETLIFT_NATIVE_CAPTURED_REGISTRATION_ARTIFACTS" in
    /*) ;;
    *)
        printf 'Artifact root must be an absolute path.\n' >&2
        exit 2
        ;;
esac
mkdir -p "$NETLIFT_NATIVE_CAPTURED_REGISTRATION_ARTIFACTS"
ARTIFACT_ROOT="$(cd "$NETLIFT_NATIVE_CAPTURED_REGISTRATION_ARTIFACTS" && pwd -P)"
case "$ARTIFACT_ROOT" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf 'Artifact root must not resolve inside shared system temp.\n' >&2
        exit 2
        ;;
esac
case "$ARTIFACT_ROOT" in
    */.copilot/session-state/*/files/*) ;;
    *)
        printf 'Artifact root must resolve under a Copilot session files directory.\n' >&2
        exit 2
        ;;
esac

WORK="$(mktemp -d "$ARTIFACT_ROOT/run.XXXXXX")"
WORK="$(cd "$WORK" && pwd -P)"
export DOTNET_CLI_HOME="$WORK/dotnet-cli-home"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1
export TMPDIR="$WORK/tmp"
export NUGET_PACKAGES="$WORK/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$WORK/nuget-http-cache"
mkdir -p "$DOTNET_CLI_HOME" "$TMPDIR" "$NUGET_PACKAGES" \
    "$NUGET_HTTP_CACHE_PATH" "$WORK/empty-nuget-source"

if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    printf 'This spike runner requires macOS arm64.\n' >&2
    exit 2
fi
if [[ "$(dotnet --version)" != "11.0.100-rc.1.26425.128" ]]; then
    printf 'Expected .NET SDK 11.0.100-rc.1.26425.128.\n' >&2
    exit 2
fi
if ! command -v clang >/dev/null 2>&1; then
    printf 'clang is required.\n' >&2
    exit 2
fi

ARTIFACTS="$WORK/dotnet-artifacts"
NATIVE="$WORK/native"
mkdir -p "$NATIVE"
restore_and_build() {
    local project="$1"
    shift
    dotnet restore "$project" \
        --source "$WORK/empty-nuget-source" \
        --artifacts-path "$ARTIFACTS" \
        -p:NetLiftManagedHostDispatchSmoke=true \
        --verbosity quiet
    dotnet build "$project" \
        --configuration Release \
        --no-restore \
        --artifacts-path "$ARTIFACTS" \
        -p:NetLiftManagedHostDispatchSmoke=true \
        --nologo \
        --verbosity quiet \
        "$@"
}

restore_and_build "$SMOKE_PROJECT"
restore_and_build "$BRIDGE_PROJECT"

find_artifact() {
    local search_root="$1"
    local file_name="$2"
    local match
    match="$(find "$search_root" -type f -name "$file_name" -print -quit)"
    if [[ -z "$match" ]]; then
        printf 'Build artifact not found: %s under %s\n' "$file_name" "$search_root" >&2
        exit 1
    fi
    printf '%s\n' "$match"
}

NEW_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
OLD_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
NEW_OUTPUT="$ARTIFACTS/bin/$NEW_ASSEMBLY/release"
OLD_OUTPUT="$ARTIFACTS/bin/$OLD_ASSEMBLY/release"
BRIDGE_OUTPUT="$ARTIFACTS/bin/NetLift.NativeCapturedRegistrationSeam.Bridge/release"
ROOT_OUTPUT="$ARTIFACTS/bin/NetLift.ManagedHost.CapturedModule/release"
GENERATED_OUTPUT="$ARTIFACTS/bin/NetLift.Negotiation.AsyncHost.Managed.New/release"
HOST_COMPONENT="$(find_artifact "$ARTIFACTS/bin/NetLift.ManagedHost" NetLift.ManagedHost.dll)"
LOCAL_DLL="$(find_artifact "$NEW_OUTPUT" "$NEW_ASSEMBLY.dll")"
RUNTIME_CONFIG="$(find_artifact "$NEW_OUTPUT" "$NEW_ASSEMBLY.runtimeconfig.json")"
PEER_DLL="$(find_artifact "$OLD_OUTPUT" "$OLD_ASSEMBLY.dll")"
BRIDGE_DLL="$(find_artifact "$BRIDGE_OUTPUT" NetLift.Negotiation.NativeHost.AsyncRuntime.LiveHost.New.dll)"
ROOT_IMAGE="$(find_artifact "$ROOT_OUTPUT" NetLift.ManagedHost.CapturedAppModule.dll)"
PRIVATE_IMAGE="$(find_artifact "$ROOT_OUTPUT" NetLift.ManagedHost.CapturedPrivateDependency.dll)"
GENERATED_IMAGE="$(find_artifact "$GENERATED_OUTPUT" NetLift.Negotiation.AsyncHost.Managed.New.dll)"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd -P)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Matching osx-arm64 nethost pack is missing for runtime %s.\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

CAPTURE_STAGE="$WORK/capture-stage"
mkdir -p "$CAPTURE_STAGE"
cp -R "$(dirname "$ROOT_IMAGE")/." "$CAPTURE_STAGE/"
STAGED_ROOT="$CAPTURE_STAGE/NetLift.ManagedHost.CapturedAppModule.dll"
STAGED_PRIVATE="$CAPTURE_STAGE/NetLift.ManagedHost.CapturedPrivateDependency.dll"
STAGED_PROVIDER="$CAPTURE_STAGE/NetLift.Negotiation.AsyncHost.Managed.New.dll"
EXECUTABLE="$NATIVE/captured-registration-runner"
clang -std=c11 -Wall -Wextra -Werror -pthread \
    -DDEBUG=1 \
    -DNETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1 \
    -DNETLIFT_NATIVEHOST_SOURCE_V2=1 \
    -DNLV5_PRIVATE_HOST_COORDINATOR_TESTING=1 \
    -DNETLIFT_ASYNC_RUNTIME_NEW=1 \
    -I "$CORE_DIR" \
    -I "$HOST_DIR" \
    -I "$ROOT/tests/NetLift.Negotiation.NativeHost" \
    -I "$HOST_NATIVE" \
    "$CORE_DIR/coreclr_bootstrap.c" \
    "$CORE_DIR/managed_bridge_v2.c" \
    "$CORE_DIR/managed_app_host_v1.c" \
    "$CORE_DIR/managed_app_endpoint_authority_v1.c" \
    "$CORE_DIR/managed_app_endpoint_transport_v1.c" \
    "$CORE_DIR/private_host_coordinator.c" \
    "$CORE_DIR/async_v2_core.c" \
    "$CORE_DIR/async_v2_lifecycle.c" \
    "$CORE_DIR/async_v2_accessor.c" \
    "$HOST_DIR/native_v2_test_provider.c" \
    "$HOST_DIR/v2_callback_route_proof.c" \
    "$SPIKE_DIR/native/captured_registration_runner.c" \
    -L "$HOST_NATIVE" -lnethost \
    -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
    -ldl \
    -o "$EXECUTABLE"

run_case() {
    "$EXECUTABLE" "$1" \
        "$DOTNET_ROOT" "$LOCAL_DLL" "$RUNTIME_CONFIG" \
        "$NEW_ASSEMBLY" "NetLift.Negotiation.NativeHost.Managed.New" \
        "$PEER_DLL" "$OLD_ASSEMBLY" "NetLift.Negotiation.NativeHost.Managed.Old" \
        "$HOST_COMPONENT" "$BRIDGE_DLL" \
        "$STAGED_ROOT" "$STAGED_PRIVATE" "$STAGED_PROVIDER" \
        "$STAGED_ROOT" "$STAGED_PRIVATE" "$STAGED_PROVIDER"
}

for case_name in positive wrong-digest missing-dependency schema-mismatch path-control; do
    rm -rf "$CAPTURE_STAGE"
    mkdir -p "$CAPTURE_STAGE"
    cp -R "$(dirname "$ROOT_IMAGE")/." "$CAPTURE_STAGE/"
    STAGED_ROOT="$CAPTURE_STAGE/NetLift.ManagedHost.CapturedAppModule.dll"
    STAGED_PRIVATE="$CAPTURE_STAGE/NetLift.ManagedHost.CapturedPrivateDependency.dll"
    STAGED_PROVIDER="$CAPTURE_STAGE/NetLift.Negotiation.AsyncHost.Managed.New.dll"
    run_case "$case_name"
done

printf '%s\n' 'PASS native captured-registration seam and negative matrix.'
printf 'Artifacts and logs: %s\n' "$WORK"
