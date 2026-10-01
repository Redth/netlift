#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
OLD_PROJECT="$HOST_DIR/Managed.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj"
NEW_PROJECT="$HOST_DIR/Managed.New/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj"

if [[ -z "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && pwd)"
DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
NATIVE_OUTPUT="$ARTIFACT_ROOT/v2-module-composition"
BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")
OLD_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
NEW_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
OLD_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.Old"
NEW_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.New"
OLD_OUTPUT="$DOTNET_ARTIFACTS/bin/$OLD_ASSEMBLY/release"
NEW_OUTPUT="$DOTNET_ARTIFACTS/bin/$NEW_ASSEMBLY/release"

mkdir -p "$NATIVE_OUTPUT"
clang -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/private_host_coordinator_header_smoke.c"
clang++ -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/private_host_coordinator_header_smoke.cpp"
clang -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/private_v2_layout_smoke.c"
clang++ -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/private_v2_layout_smoke.cpp"
printf 'PASS native-owned private V2 table and callback ABI C11/C++17 layout smoke\n'

dotnet build "$OLD_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_PROJECT" -c Release "${BUILD_ARGS[@]}"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

for direction in old new; do
    if [[ "$direction" == old ]]; then
        define=(-UNETLIFT_ASYNC_RUNTIME_NEW)
        local_assembly="$OLD_ASSEMBLY"
        local_namespace="$OLD_NAMESPACE"
        local_output="$OLD_OUTPUT"
        peer_assembly="$NEW_ASSEMBLY"
        peer_namespace="$NEW_NAMESPACE"
        peer_output="$NEW_OUTPUT"
    else
        define=(-DNETLIFT_ASYNC_RUNTIME_NEW=1)
        local_assembly="$NEW_ASSEMBLY"
        local_namespace="$NEW_NAMESPACE"
        local_output="$NEW_OUTPUT"
        peer_assembly="$OLD_ASSEMBLY"
        peer_namespace="$OLD_NAMESPACE"
        peer_output="$OLD_OUTPUT"
    fi

    executable="$NATIVE_OUTPUT/netlift-v2-module-composition-$direction"
    clang -std=c11 -Wall -Wextra -Werror -pthread \
        -DDEBUG=1 \
        -DNETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1 \
        -DNETLIFT_NATIVEHOST_SOURCE_V2=1 \
        -DNLV5_PRIVATE_HOST_COORDINATOR_TESTING=1 \
        "${define[@]}" \
        -I "$CORE_DIR" \
        -I "$HOST_DIR" \
        -I "$ROOT/tests/NetLift.Negotiation.NativeHost" \
        -I "$HOST_NATIVE" \
        "$CORE_DIR/coreclr_bootstrap.c" \
        "$CORE_DIR/managed_app_host_v1.c" \
        "$CORE_DIR/managed_app_endpoint_authority_v1.c" \
        "$CORE_DIR/managed_app_endpoint_transport_v1.c" \
        "$CORE_DIR/managed_bridge_v2.c" \
        "$CORE_DIR/private_host_coordinator.c" \
        "$CORE_DIR/async_v2_core.c" \
        "$CORE_DIR/async_v2_lifecycle.c" \
        "$CORE_DIR/async_v2_accessor.c" \
        "$HOST_DIR/native_v2_test_provider.c" \
        "$HOST_DIR/v2_callback_route_proof.c" \
        "$HOST_DIR/v2_module_composition_runner.c" \
        -L "$HOST_NATIVE" -lnethost \
        -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
        -ldl \
        -o "$executable"

    "$executable" \
        "$DOTNET_ROOT" \
        "$local_output/$local_assembly.dll" \
        "$local_output/$local_assembly.runtimeconfig.json" \
        "$local_assembly" \
        "$local_namespace" \
        "$peer_output/$peer_assembly.dll" \
        "$peer_assembly" \
        "$peer_namespace"
done
