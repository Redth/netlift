#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
OLD_PROJECT="$HOST_DIR/Managed.Old/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old.csproj"
NEW_PROJECT="$HOST_DIR/Managed.New/NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New.csproj"
OLD_MODULE_PROJECT="$ROOT/tests/NetLift.Negotiation.AsyncHost.Managed.Old.V2/NetLift.Negotiation.AsyncHost.Managed.Old.V2.csproj"
NEW_MODULE_PROJECT="$ROOT/tests/NetLift.Negotiation.AsyncHost.Managed.New/NetLift.Negotiation.AsyncHost.Managed.New.csproj"

if [[ -z "${NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_COORDINATOR_ARTIFACTS" && pwd)"
DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
NATIVE_OUTPUT="$ARTIFACT_ROOT/native"
BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")
OLD_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
NEW_ASSEMBLY="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
OLD_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.Old"
NEW_NAMESPACE="NetLift.Negotiation.NativeHost.Managed.New"
OLD_MODULE="NetLift.Negotiation.AsyncHost.Managed.Old.V2"
NEW_MODULE="NetLift.Negotiation.AsyncHost.Managed.New"
OLD_OUTPUT="$DOTNET_ARTIFACTS/bin/$OLD_ASSEMBLY/release"
NEW_OUTPUT="$DOTNET_ARTIFACTS/bin/$NEW_ASSEMBLY/release"
OLD_MODULE_OUTPUT="$DOTNET_ARTIFACTS/bin/$OLD_MODULE/release"
NEW_MODULE_OUTPUT="$DOTNET_ARTIFACTS/bin/$NEW_MODULE/release"

mkdir -p "$NATIVE_OUTPUT"
clang -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/private_host_coordinator_header_smoke.c"
clang++ -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/private_host_coordinator_header_smoke.cpp"
printf 'PASS private coordinator C11/C++17 header/layout smoke\n'

dotnet build "$OLD_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$OLD_MODULE_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_MODULE_PROJECT" -c Release "${BUILD_ARGS[@]}"

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
        local_module="$OLD_MODULE_OUTPUT/$OLD_MODULE.dll"
        peer_module="$NEW_MODULE_OUTPUT/$NEW_MODULE.dll"
    else
        define=(-DNETLIFT_ASYNC_RUNTIME_NEW=1)
        local_assembly="$NEW_ASSEMBLY"
        local_namespace="$NEW_NAMESPACE"
        local_output="$NEW_OUTPUT"
        peer_assembly="$OLD_ASSEMBLY"
        peer_namespace="$OLD_NAMESPACE"
        peer_output="$OLD_OUTPUT"
        local_module="$NEW_MODULE_OUTPUT/$NEW_MODULE.dll"
        peer_module="$OLD_MODULE_OUTPUT/$OLD_MODULE.dll"
    fi

    local_dll="$local_output/$local_assembly.dll"
    local_runtime_config="$local_output/$local_assembly.runtimeconfig.json"
    peer_dll="$peer_output/$peer_assembly.dll"
    host_component="$local_output/NetLift.ManagedHost.dll"
    executable="$NATIVE_OUTPUT/managed-app-host-v1-coordinator-$direction"
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
        "$HOST_DIR/managed_app_host_v1_coordinator_runner.c" \
        -L "$HOST_NATIVE" -lnethost \
        -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
        -ldl \
        -o "$executable"

    "$executable" success \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" wrong-local-pin \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$peer_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" missing-module \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$ARTIFACT_ROOT/missing/ManagedAppModule.dll" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" missing-provider \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" Missing.Provider.Type
    "$executable" corrupt-nlps \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" corrupt-nlmr \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" rollback-after-registration \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" rollback-after-result-context \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" rollback-busy-close \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-selection-invalid \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-selection-success \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-selection-wrong-path \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-selection-wrong-provider \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-selection-register-failure \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-selection-busy-close \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-activation-success \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-activation-descriptor \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-activation-manifest \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-activation-table \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-activation-callback \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-activation-lease \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-activation-free \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" manifest-free-failure \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-table \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-success \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-wrong-local-pin \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-peer-pin \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-missing-module \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$ARTIFACT_ROOT/missing/ManagedAppModule.dll" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
    "$executable" authority-missing-provider \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" Missing.Provider.Type
    "$executable" authority-busy-close \
        "$DOTNET_ROOT" "$local_dll" "$local_runtime_config" \
        "$local_assembly" "$local_namespace" \
        "$peer_dll" "$peer_assembly" "$peer_namespace" \
        "$host_component" "$local_module" \
        NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider
done
