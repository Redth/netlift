#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncResult"
OLD_PROJECT="$HOST_DIR/Managed.Old/NetLift.Negotiation.NativeHost.Async.Managed.Old.csproj"
NEW_PROJECT="$HOST_DIR/Managed.New/NetLift.Negotiation.NativeHost.Async.Managed.New.csproj"

if [[ -z "${NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS" && pwd)"
DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
HOST_OUTPUT="$ARTIFACT_ROOT/native-host"
OLD_OUTPUT="$DOTNET_ARTIFACTS/bin/NetLift.Negotiation.NativeHost.Async.Managed.Old/release"
NEW_OUTPUT="$DOTNET_ARTIFACTS/bin/NetLift.Negotiation.NativeHost.Async.Managed.New/release"
BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")

mkdir -p "$HOST_OUTPUT"
dotnet build "$OLD_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_PROJECT" -c Release "${BUILD_ARGS[@]}"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"

if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

clang -std=c11 -Wall -Wextra -Werror -pthread \
    -I "$HOST_NATIVE" \
    "$HOST_DIR/managed_result_host.c" \
    -L "$HOST_NATIVE" -lnethost \
    -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
    -ldl \
    -o "$HOST_OUTPUT/netlift-managed-result-v5-host"

"$HOST_OUTPUT/netlift-managed-result-v5-host" \
    "$DOTNET_ROOT" \
    "$OLD_OUTPUT/NetLift.Negotiation.NativeHost.Async.Managed.Old.dll" \
    "$OLD_OUTPUT/NetLift.Negotiation.NativeHost.Async.Managed.Old.runtimeconfig.json" \
    "$NEW_OUTPUT/NetLift.Negotiation.NativeHost.Async.Managed.New.dll" \
    "$NEW_OUTPUT/NetLift.Negotiation.NativeHost.Async.Managed.New.runtimeconfig.json"
