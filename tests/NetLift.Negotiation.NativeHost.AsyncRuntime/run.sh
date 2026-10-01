#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
APPLE_TYPED="$ROOT/spikes/apple/NativeSwiftHost/typed"
OLD_PROJECT="$HOST_DIR/Managed.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj"
NEW_PROJECT="$HOST_DIR/Managed.New/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj"

if [[ -z "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && pwd)"
DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
HOST_OUTPUT="$ARTIFACT_ROOT/native-host"
BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")

OLD_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
NEW_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
OLD_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.Old"
NEW_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.New"
OLD_OUTPUT="$DOTNET_ARTIFACTS/bin/$OLD_ASSEMBLY/release"
NEW_OUTPUT="$DOTNET_ARTIFACTS/bin/$NEW_ASSEMBLY/release"

mkdir -p "$HOST_OUTPUT"
clang -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/provider_header_smoke.c"
clang++ -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/provider_header_smoke.cpp"
if [[ "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_PREBUILT_MANAGED:-0}" == "1" ]]; then
    OLD_INPUT="$HOST_DIR/Managed.Old/bin/Release/net11.0"
    NEW_INPUT="$HOST_DIR/Managed.New/bin/Release/net11.0"
    if [[ ! -f "$OLD_INPUT/$OLD_ASSEMBLY.dll" || ! -f "$NEW_INPUT/$NEW_ASSEMBLY.dll" ]]; then
        printf 'Prebuilt managed inputs are missing; build both managed fixtures first.\n' >&2
        exit 1
    fi
    mkdir -p "$OLD_OUTPUT" "$NEW_OUTPUT"
    cp -R "$OLD_INPUT/." "$OLD_OUTPUT/"
    cp -R "$NEW_INPUT/." "$NEW_OUTPUT/"
    printf 'Using prebuilt Old/New managed fixture outputs; native artifacts remain isolated.\n'
else
    dotnet build "$OLD_PROJECT" -c Release "${BUILD_ARGS[@]}"
    dotnet build "$NEW_PROJECT" -c Release "${BUILD_ARGS[@]}"
fi

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

build_native_host() {
    local direction="$1"
    local define=()
    local lifecycle_define=(-DNETLIFT_NATIVEHOST_SOURCE_V2=0)
    local bootstrap_sources=()
    local lifecycle_sources=()
    local local_assembly local_namespace local_output local_config
    local peer_assembly peer_namespace peer_output
    if [[ "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP:-0}" == "1" ]]; then
        bootstrap_sources=(
            "$CORE_DIR/coreclr_bootstrap.c"
            "$CORE_DIR/managed_app_host_v1.c"
            "$CORE_DIR/managed_app_endpoint_authority_v1.c"
            "$CORE_DIR/managed_app_endpoint_transport_v1.c"
            "$HOST_DIR/source_coreclr_test_adapter.c"
        )
    else
        bootstrap_sources=("$HOST_DIR/runtime_host.c")
    fi
    if [[ "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_V2:-0}" == "1" ]]; then
        if [[ "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP:-0}" != "1" ]]; then
            printf 'Source V2 lifecycle selection requires the source CoreCLR bootstrap.\n' >&2
            exit 2
        fi
        lifecycle_define=(-DNETLIFT_NATIVEHOST_SOURCE_V2=1)
        lifecycle_sources=(
            "$CORE_DIR/async_v2_lifecycle.c"
            "$CORE_DIR/async_v2_accessor.c"
        )
    else
        lifecycle_sources=(
            "$APPLE_TYPED/CallbackLifecycleV2State.c"
            "$APPLE_TYPED/CallbackLifecycleV2Accessor.c"
        )
    fi
    if [[ "$direction" == old ]]; then
        define=(-UNETLIFT_ASYNC_RUNTIME_NEW)
        local_assembly="$OLD_ASSEMBLY"
        local_namespace="$OLD_NAMESPACE"
        local_output="$OLD_OUTPUT"
        local_config="$OLD_OUTPUT/$OLD_ASSEMBLY.runtimeconfig.json"
        peer_assembly="$NEW_ASSEMBLY"
        peer_namespace="$NEW_NAMESPACE"
        peer_output="$NEW_OUTPUT"
    else
        define=(-DNETLIFT_ASYNC_RUNTIME_NEW=1)
        local_assembly="$NEW_ASSEMBLY"
        local_namespace="$NEW_NAMESPACE"
        local_output="$NEW_OUTPUT"
        local_config="$NEW_OUTPUT/$NEW_ASSEMBLY.runtimeconfig.json"
        peer_assembly="$OLD_ASSEMBLY"
        peer_namespace="$OLD_NAMESPACE"
        peer_output="$OLD_OUTPUT"
    fi

    local executable="$HOST_OUTPUT/netlift-async-runtime-$direction"
    clang -std=c11 -Wall -Wextra -Werror -pthread \
        -DDEBUG=1 \
        -DNETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1 \
        "${lifecycle_define[@]}" \
        "${define[@]}" \
        -I "$CORE_DIR" \
        -I "$HOST_DIR/include" \
        -I "$HOST_DIR" \
        -I "$ROOT/tests/NetLift.Negotiation.NativeHost" \
        -I "$APPLE_TYPED" \
        -I "$APPLE_TYPED/GeneratedTyped" \
        -I "$HOST_NATIVE" \
        "${bootstrap_sources[@]}" \
        "$HOST_DIR/async_runtime_host.c" \
        "$HOST_DIR/facade_compat.c" \
        "$CORE_DIR/async_v2_core.c" \
        "$HOST_DIR/test_v2_provider.c" \
        "${lifecycle_sources[@]}" \
        -L "$HOST_NATIVE" -lnethost \
        -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
        -ldl \
        -o "$executable"

    "$executable" \
        "$DOTNET_ROOT" \
        "$local_output/$local_assembly.dll" \
        "$local_config" \
        "$local_assembly" \
        "$local_namespace" \
        "$peer_output/$peer_assembly.dll" \
        "$peer_assembly" \
        "$peer_namespace"

    if [[ "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_USE_SOURCE_BOOTSTRAP:-0}" == "1" ]]; then
        NETLIFT_NATIVEHOST_ASYNC_RUNTIME_EXPECT_MISSING_EXPORT=1 \
            "$executable" \
            "$DOTNET_ROOT" \
            "$local_output/$local_assembly.dll" \
            "$local_config" \
            "$local_assembly" \
            "$local_namespace" \
            "$peer_output/$peer_assembly.dll" \
            "$peer_assembly" \
            "$peer_namespace"
    fi
}

build_native_host old
build_native_host new
