#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost"
OLD_PROJECT_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.Managed.Old"
NEW_PROJECT_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.Managed.New"
OLD_PROJECT="$OLD_PROJECT_DIR/NetLift.Negotiation.NativeHost.Managed.Old.csproj"
NEW_PROJECT="$NEW_PROJECT_DIR/NetLift.Negotiation.NativeHost.Managed.New.csproj"
PIN_LOCK="${NETLIFT_V5_PEER_PIN_LOCK:-$ROOT/spikes/build-dev-loop/NativeHost/peer-provenance.lock}"
PIN_STAGE="$ROOT/spikes/build-dev-loop/stage-v5-peer-pin.py"
BUILD_ARGS=(--nologo)

if [[ -n "${NETLIFT_NATIVEHOST_ARTIFACTS:-}" ]]; then
    ARTIFACT_ROOT="$(cd "$(dirname "$NETLIFT_NATIVEHOST_ARTIFACTS")" && pwd)/$(basename "$NETLIFT_NATIVEHOST_ARTIFACTS")"
    DOTNET_ARTIFACTS="$ARTIFACT_ROOT/dotnet-artifacts"
    OLD_OUTPUT="$DOTNET_ARTIFACTS/bin/NetLift.Negotiation.NativeHost.Managed.Old/release"
    NEW_OUTPUT="$DOTNET_ARTIFACTS/bin/NetLift.Negotiation.NativeHost.Managed.New/release"
    OLD_GENERATED_ROOT="$DOTNET_ARTIFACTS/obj/NetLift.Negotiation.NativeHost.Managed.Old"
    NEW_GENERATED_ROOT="$DOTNET_ARTIFACTS/obj/NetLift.Negotiation.NativeHost.Managed.New"
    HOST_OUTPUT="$ARTIFACT_ROOT/native-host"
    PIN_OUTPUT="$ARTIFACT_ROOT/peer-pin"
    BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")
else
    OLD_OUTPUT="$OLD_PROJECT_DIR/bin/Release/net11.0"
    NEW_OUTPUT="$NEW_PROJECT_DIR/bin/Release/net11.0"
    OLD_GENERATED_ROOT="$OLD_PROJECT_DIR/obj"
    NEW_GENERATED_ROOT="$NEW_PROJECT_DIR/obj"
    HOST_OUTPUT="$HOST_DIR/bin"
    PIN_OUTPUT="$HOST_OUTPUT"
fi

mkdir -p "$HOST_OUTPUT"
rm -f "$HOST_OUTPUT/netlift-compatible-start-v5-host"
dotnet build "$OLD_PROJECT" -c Release "${BUILD_ARGS[@]}"
dotnet build "$NEW_PROJECT" -c Release "${BUILD_ARGS[@]}"

find_generated_contract() {
    local generated_root="$1"
    local result
    result="$(find "$generated_root" -type f \
        -path '*/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs' -print -quit)"
    if [[ -z "$result" ]]; then
        printf 'Could not find generated v5 LiftContract.g.cs under %s\n' "$generated_root" >&2
        exit 1
    fi
    printf '%s\n' "$result"
}

OLD_GENERATED="$(find_generated_contract "$OLD_GENERATED_ROOT")"
NEW_GENERATED="$(find_generated_contract "$NEW_GENERATED_ROOT")"
python3 "$PIN_STAGE" "$PIN_LOCK" "$OLD_GENERATED" "$NEW_GENERATED" "$PIN_OUTPUT"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"

if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

mkdir -p "$HOST_OUTPUT"
printf 'NATIVE-HOST-LINK: clang\n'
clang -std=c11 -Wall -Wextra -Werror -pthread \
    -I "$HOST_NATIVE" \
    -I "$PIN_OUTPUT" \
    -I "$HOST_DIR" \
    "$HOST_DIR/host.c" \
    -L "$HOST_NATIVE" -lnethost \
    -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
    -ldl \
    -o "$HOST_OUTPUT/netlift-compatible-start-v5-host"

"$HOST_OUTPUT/netlift-compatible-start-v5-host" \
    "$DOTNET_ROOT" \
    "$OLD_OUTPUT/NetLift.Negotiation.NativeHost.Managed.Old.dll" \
    "$OLD_OUTPUT/NetLift.Negotiation.NativeHost.Managed.Old.runtimeconfig.json" \
    "$NEW_OUTPUT/NetLift.Negotiation.NativeHost.Managed.New.dll" \
    "$NEW_OUTPUT/NetLift.Negotiation.NativeHost.Managed.New.runtimeconfig.json"
