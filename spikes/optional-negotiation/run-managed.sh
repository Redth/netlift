#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPIKE_DIR="$ROOT/spikes/optional-negotiation"
MANAGED_DIR="$SPIKE_DIR/managed"
MANAGED_OUTPUT="$MANAGED_DIR/bin/Release/net11.0"
HOST_OUTPUT="$SPIKE_DIR/.artifacts"

dotnet build "$MANAGED_DIR/NetLift.OptionalNegotiation.Managed.csproj" -c Release
python3 "$MANAGED_DIR/prepare_negative_manifests.py"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
case "$(uname -m)" in
    arm64) HOST_RID="osx-arm64" ;;
    x86_64) HOST_RID="osx-x64" ;;
    *)
        printf 'Unsupported macOS host architecture: %s\n' "$(uname -m)" >&2
        exit 1
        ;;
esac
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.$HOST_RID/$RUNTIME_VERSION/runtimes/$HOST_RID/native"

if [[ -z "$RUNTIME_VERSION" || ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the .NET 11 %s nethost pack for runtime %s\n' \
        "$HOST_RID" "$RUNTIME_VERSION" >&2
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
    -o "$HOST_OUTPUT/optional-negotiation-managed-host"

"$HOST_OUTPUT/optional-negotiation-managed-host" \
    "$DOTNET_ROOT" \
    "$MANAGED_OUTPUT/NetLift.OptionalNegotiation.Managed.dll" \
    "$MANAGED_OUTPUT/NetLift.OptionalNegotiation.Managed.runtimeconfig.json" \
    "$SPIKE_DIR/v1.schema.json" \
    "$SPIKE_DIR/v2.schema.json" \
    "$HOST_OUTPUT/mandatory-addition.schema.json" \
    "$HOST_OUTPUT/unsupported-default.schema.json" \
    "$HOST_OUTPUT/same-minor-addition.schema.json" \
    "$HOST_OUTPUT/abi-major-mismatch.schema.json" \
    "$HOST_OUTPUT/schema-major-mismatch.schema.json" \
    "$HOST_OUTPUT/renumbered-field.schema.json" \
    "$HOST_OUTPUT/changed-type.schema.json" \
    "$HOST_OUTPUT/changed-nullability.schema.json"
