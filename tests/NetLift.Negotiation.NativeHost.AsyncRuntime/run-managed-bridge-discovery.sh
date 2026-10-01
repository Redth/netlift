#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
APPLE_TYPED="$ROOT/spikes/apple/NativeSwiftHost/typed"
OLD_HOST_PROJECT="$HOST_DIR/Managed.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj"
NEW_HOST_PROJECT="$HOST_DIR/Managed.New/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj"
OLD_MODULE_PROJECT="$ROOT/tests/NetLift.Negotiation.AsyncHost.Managed.Old/NetLift.Negotiation.AsyncHost.Managed.Old.csproj"
NEW_MODULE_PROJECT="$ROOT/tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj"

if [[ -z "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && pwd)"
DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
NATIVE_OUTPUT="$ARTIFACT_ROOT/managed-bridge-native"
BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")
OLD_HOST_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
NEW_HOST_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
BRIDGE_ASSEMBLY="NetLift.Negotiation"
OLD_MODULE_ASSEMBLY="NetLift.Negotiation.AsyncHost.Managed.Old"
NEW_MODULE_ASSEMBLY="NetLift.Negotiation.AsyncHost.Managed.New"

mkdir -p "$NATIVE_OUTPUT"
clang -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/managed_bridge_v1_header_smoke.c"
clang++ -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/managed_bridge_v1_header_smoke.cpp"
printf 'PASS managed bridge C11/C++17 table layout assertions\n'

dotnet build "$OLD_HOST_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_HOST_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$OLD_MODULE_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_MODULE_PROJECT" -c Release "${BUILD_ARGS[@]}"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

BRIDGE_DLL="$DOTNET_ARTIFACTS/bin/$BRIDGE_ASSEMBLY/release/$BRIDGE_ASSEMBLY.dll"
OLD_HOST_DIR="$DOTNET_ARTIFACTS/bin/$OLD_HOST_ASSEMBLY/release"
NEW_HOST_DIR="$DOTNET_ARTIFACTS/bin/$NEW_HOST_ASSEMBLY/release"
OLD_MODULE_DLL="$DOTNET_ARTIFACTS/bin/$OLD_MODULE_ASSEMBLY/release/$OLD_MODULE_ASSEMBLY.dll"
NEW_MODULE_DLL="$DOTNET_ARTIFACTS/bin/$NEW_MODULE_ASSEMBLY/release/$NEW_MODULE_ASSEMBLY.dll"

for direction in old new; do
    if [[ "$direction" == old ]]; then
        host_dir="$OLD_HOST_DIR"
        host_assembly="$OLD_HOST_ASSEMBLY"
        host_namespace="NetLift.Negotiation.NativeHost.Managed.Old"
        module_dll="$OLD_MODULE_DLL"
        module_assembly="$OLD_MODULE_ASSEMBLY"
        define=(-UNETLIFT_ASYNC_RUNTIME_NEW)
    else
        host_dir="$NEW_HOST_DIR"
        host_assembly="$NEW_HOST_ASSEMBLY"
        host_namespace="NetLift.Negotiation.NativeHost.Managed.New"
        module_dll="$NEW_MODULE_DLL"
        module_assembly="$NEW_MODULE_ASSEMBLY"
        define=(-DNETLIFT_ASYNC_RUNTIME_NEW=1)
    fi

    for input in \
        "$host_dir/$host_assembly.dll" \
        "$host_dir/$host_assembly.runtimeconfig.json" \
        "$BRIDGE_DLL" \
        "$module_dll"; do
        if [[ ! -f "$input" ]]; then
            printf 'Required isolated managed input is missing: %s\n' "$input" >&2
            exit 1
        fi
    done

    executable="$NATIVE_OUTPUT/netlift-managed-bridge-$direction"
    clang -std=c11 -Wall -Wextra -Werror -pthread \
        -DDEBUG=1 \
        -DNETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1 \
        -DNETLIFT_NATIVEHOST_SOURCE_V2=1 \
        "${define[@]}" \
        -I "$CORE_DIR" \
        -I "$HOST_DIR/include" \
        -I "$HOST_DIR" \
        -I "$ROOT/tests/NetLift.Negotiation.NativeHost" \
        -I "$APPLE_TYPED" \
        -I "$APPLE_TYPED/GeneratedTyped" \
        -I "$HOST_NATIVE" \
        "$CORE_DIR/coreclr_bootstrap.c" \
        "$CORE_DIR/managed_app_host_v1.c" \
        "$CORE_DIR/managed_app_endpoint_authority_v1.c" \
        "$CORE_DIR/managed_app_endpoint_transport_v1.c" \
        "$CORE_DIR/managed_bridge_v1.c" \
        "$CORE_DIR/async_v2_core.c" \
        "$CORE_DIR/async_v2_lifecycle.c" \
        "$CORE_DIR/async_v2_accessor.c" \
        "$HOST_DIR/source_coreclr_test_adapter.c" \
        "$HOST_DIR/test_v2_provider.c" \
        "$HOST_DIR/managed_bridge_v1_probe.c" \
        -L "$HOST_NATIVE" -lnethost \
        -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
        -ldl \
        -o "$executable"

    for mode in positive missing-bridge missing-module hash-mismatch; do
        "$executable" \
            "$DOTNET_ROOT" \
            "$host_dir/$host_assembly.runtimeconfig.json" \
            "$host_dir/$host_assembly.dll" \
            "$host_assembly" \
            "$host_namespace" \
            "$BRIDGE_DLL" \
            "$BRIDGE_ASSEMBLY" \
            "$module_dll" \
            "$module_assembly" \
            "$mode"
    done
done
