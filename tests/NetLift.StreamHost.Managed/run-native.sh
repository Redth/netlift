#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.StreamHost.Managed"
MANAGED_PROJECT="$HOST_DIR/NetLift.StreamHost.Managed.csproj"
MANAGED_OUTPUT="$HOST_DIR/bin/Release/net11.0"
GENERATED_SOURCE="$HOST_DIR/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
PROJECTION_OUTPUT="$HOST_DIR/obj/projected"
HOST_OUTPUT="$HOST_DIR/bin/native"

if [[ "$(uname -m)" != "arm64" ]]; then
    printf 'This host runner requires macOS arm64; found %s.\n' "$(uname -m)" >&2
    exit 1
fi

dotnet build "$MANAGED_PROJECT" -c Release -warnaserror
dotnet run --project "$ROOT/src/NetLift.Projection/NetLift.Projection.csproj" -c Release -- \
    "$GENERATED_SOURCE" "$PROJECTION_OUTPUT"

DOTNET_ROOT="$(dirname "$(command -v dotnet)")"
RUNTIME_VERSION="$(dotnet --list-runtimes |
    awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
if [[ -z "$RUNTIME_VERSION" ]]; then
    printf 'Could not find an installed .NET 11 runtime.\n' >&2
    exit 1
fi
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

mkdir -p "$HOST_OUTPUT"
clang -std=c11 -Wall -Wextra -Werror -arch arm64 \
    -I "$HOST_NATIVE" \
    -I "$PROJECTION_OUTPUT" \
    -fsyntax-only "$HOST_DIR/abi_layout.c"
clang -std=c11 -Wall -Wextra -Werror -arch arm64 \
    -I "$HOST_NATIVE" \
    -I "$PROJECTION_OUTPUT" \
    "$HOST_DIR/native_host.c" \
    -L "$HOST_NATIVE" -lnethost \
    -Wl,-rpath,"$HOST_NATIVE" \
    -ldl -lpthread \
    -o "$HOST_OUTPUT/netlift-stream-host"

"$HOST_OUTPUT/netlift-stream-host" \
    "$DOTNET_ROOT" \
    "$MANAGED_OUTPUT/NetLift.StreamHost.Managed.dll" \
    "$MANAGED_OUTPUT/NetLift.StreamHost.Managed.runtimeconfig.json"
