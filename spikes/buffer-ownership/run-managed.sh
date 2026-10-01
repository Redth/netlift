#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPIKE_DIR="$ROOT/spikes/buffer-ownership"
MANAGED_DIR="$SPIKE_DIR/managed"
MANAGED_PROJECT="$MANAGED_DIR/NetLift.BufferOwnership.Managed.csproj"
MANAGED_OUTPUT="$MANAGED_DIR/bin/Release/net11.0"
HOST_OUTPUT="$SPIKE_DIR/.artifacts"

dotnet build "$MANAGED_PROJECT" -c Release

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"

if [[ -z "$RUNTIME_VERSION" || ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the .NET 11 osx-arm64 nethost pack for runtime %s\n' \
        "$RUNTIME_VERSION" >&2
    exit 1
fi

mkdir -p "$HOST_OUTPUT"
clang -std=c11 -Wall -Wextra -Werror -pthread \
    -I "$HOST_NATIVE" \
    -I "$MANAGED_DIR" \
    "$MANAGED_DIR/native_host.c" \
    -L "$HOST_NATIVE" -lnethost \
    -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
    -ldl \
    -o "$HOST_OUTPUT/buffer-ownership-managed-host"

"$HOST_OUTPUT/buffer-ownership-managed-host" \
    "$DOTNET_ROOT" \
    "$MANAGED_OUTPUT/NetLift.BufferOwnership.Managed.dll" \
    "$MANAGED_OUTPUT/NetLift.BufferOwnership.Managed.runtimeconfig.json"
