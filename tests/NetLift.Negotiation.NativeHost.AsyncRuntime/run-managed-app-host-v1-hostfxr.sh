#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
MANAGED_HOST_PROJECT="$ROOT/src/NetLift.ManagedHost/NetLift.ManagedHost.csproj"
OLD_PROJECT="$HOST_DIR/Managed.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj"
NEW_PROJECT="$HOST_DIR/Managed.New/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj"

if [[ -z "${NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_HOSTFXR_ARTIFACTS" && pwd)"
DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
NATIVE_OUTPUT="$ARTIFACT_ROOT/hostfxr"
BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")
OLD_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
NEW_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
OLD_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.Old"
NEW_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.New"
OLD_OUTPUT="$DOTNET_ARTIFACTS/bin/$OLD_ASSEMBLY/release"
NEW_OUTPUT="$DOTNET_ARTIFACTS/bin/$NEW_ASSEMBLY/release"
MANAGED_HOST_OUTPUT="$DOTNET_ARTIFACTS/bin/NetLift.ManagedHost/release"

mkdir -p "$NATIVE_OUTPUT"
cc=${CC:-clang}
cxx=${CXX:-clang++}

"$cc" -std=c11 -Wall -Wextra -Werror \
    -I "$CORE_DIR" \
    "$CORE_DIR/managed_app_host_v1.c" \
    "$HOST_DIR/managed_app_host_v1_table_test.c" \
    -o "$NATIVE_OUTPUT/managed-app-host-v1-table-test"
"$NATIVE_OUTPUT/managed-app-host-v1-table-test"

"$cc" -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/managed_app_host_v1_header_smoke.c"
"$cxx" -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/managed_app_host_v1_header_smoke.cpp"
printf 'PASS managed app host V1 C11/C++17 header/layout smoke\n'

dotnet build "$MANAGED_HOST_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$OLD_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_PROJECT" -c Release "${BUILD_ARGS[@]}"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi
MANAGED_HOST_DLL="$MANAGED_HOST_OUTPUT/NetLift.ManagedHost.dll"
if [[ ! -f "$MANAGED_HOST_DLL" ]]; then
    printf 'Could not find ManagedHost component at %s\n' "$MANAGED_HOST_DLL" >&2
    exit 1
fi

for direction in old new; do
    if [[ "$direction" == old ]]; then
        local_assembly="$OLD_ASSEMBLY"
        local_namespace="$OLD_NAMESPACE"
        local_output="$OLD_OUTPUT"
    else
        local_assembly="$NEW_ASSEMBLY"
        local_namespace="$NEW_NAMESPACE"
        local_output="$NEW_OUTPUT"
    fi

    executable="$NATIVE_OUTPUT/managed-app-host-v1-hostfxr-$direction"
    "$cc" -std=c11 -Wall -Wextra -Werror -pthread \
        -DDEBUG=1 \
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
        "$HOST_DIR/managed_app_host_v1_hostfxr_runner.c" \
        -L "$HOST_NATIVE" -lnethost \
        -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
        -ldl \
        -o "$executable"

    "$executable" \
        "$DOTNET_ROOT" \
        "$local_output/$local_assembly.runtimeconfig.json" \
        "$local_output/$local_assembly.dll" \
        "$local_assembly" \
        "$local_namespace" \
        "$MANAGED_HOST_DLL"
done
