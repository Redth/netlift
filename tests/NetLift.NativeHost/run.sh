#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.NativeHost"
MANAGED_PROJECT="$ROOT/tests/NetLift.NativeHost.Managed/NetLift.NativeHost.Managed.csproj"
MANAGED_OUTPUT="$ROOT/tests/NetLift.NativeHost.Managed/bin/Release/net11.0"
GENERATED_SOURCE="$ROOT/tests/NetLift.NativeHost.Managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
PROJECTION_OUTPUT="$ROOT/tests/NetLift.NativeHost.Managed/obj/projected"
HOST_OUTPUT="$HOST_DIR/bin"

dotnet build "$MANAGED_PROJECT" -c Release
dotnet run --project "$ROOT/src/NetLift.Projection/NetLift.Projection.csproj" -c Release -- \
    "$GENERATED_SOURCE" "$PROJECTION_OUTPUT"
printf '%s\n' \
    'module NetLiftNativeHostABI {' \
    '    header "netlift.h"' \
    '    export *' \
    '}' > "$PROJECTION_OUTPUT/module.modulemap"

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"

if [[ ! -f "$HOST_NATIVE/libnethost.dylib" ]]; then
    printf 'Could not find the osx-arm64 nethost pack for runtime %s\n' "$RUNTIME_VERSION" >&2
    exit 1
fi

mkdir -p "$HOST_OUTPUT"
clang -std=c11 -Wall -Wextra -Werror \
    -I "$HOST_NATIVE" \
    -I "$PROJECTION_OUTPUT" \
    -c "$HOST_DIR/host.c" \
    -o "$HOST_OUTPUT/host.o"

swiftc -swift-version 6 \
    -I "$PROJECTION_OUTPUT" \
    -Xcc -fmodule-map-file="$PROJECTION_OUTPUT/module.modulemap" \
    "$PROJECTION_OUTPUT/NetLift.swift" \
    "$HOST_DIR/SwiftHost.swift" \
    "$HOST_OUTPUT/host.o" \
    -L "$HOST_NATIVE" -lnethost \
    -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
    -ldl \
    -o "$HOST_OUTPUT/netlift-native-host"

"$HOST_OUTPUT/netlift-native-host" \
    "$DOTNET_ROOT" \
    "$MANAGED_OUTPUT/NetLift.NativeHost.Managed.dll" \
    "$MANAGED_OUTPUT/NetLift.NativeHost.Managed.runtimeconfig.json"

TYPED_PROJECT="$ROOT/tests/NetLift.NativeHost.TypedReply.Managed/NetLift.NativeHost.TypedReply.Managed.csproj"
TYPED_MANAGED_OUTPUT="$ROOT/tests/NetLift.NativeHost.TypedReply.Managed/bin/Release/net11.0"
TYPED_GENERATED_SOURCE="$ROOT/tests/NetLift.NativeHost.TypedReply.Managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
TYPED_PROJECTION_OUTPUT="$ROOT/tests/NetLift.NativeHost.TypedReply.Managed/obj/projected"
dotnet build "$TYPED_PROJECT" -c Release
dotnet run --project "$ROOT/src/NetLift.Projection/NetLift.Projection.csproj" -c Release -- \
    "$TYPED_GENERATED_SOURCE" "$TYPED_PROJECTION_OUTPUT"
printf '%s\n' \
    'module NetLiftNativeHostABI {' \
    '    header "netlift.h"' \
    "    header \"$HOST_DIR/typed_reply_api.h\"" \
    '    export *' \
    '}' > "$TYPED_PROJECTION_OUTPUT/module.modulemap"

swiftc -swift-version 6 -typecheck \
    -I "$TYPED_PROJECTION_OUTPUT" \
    -I "$HOST_DIR" \
    -Xcc -fmodule-map-file="$TYPED_PROJECTION_OUTPUT/module.modulemap" \
    "$TYPED_PROJECTION_OUTPUT/NetLift.swift"
clang -std=c11 -Wall -Wextra -Werror \
    -I "$HOST_NATIVE" \
    -I "$HOST_DIR" \
    -I "$TYPED_PROJECTION_OUTPUT" \
    -c "$HOST_DIR/typed_reply_host.c" \
    -o "$HOST_OUTPUT/typed_reply_host.o"
swiftc -swift-version 6 \
    -I "$TYPED_PROJECTION_OUTPUT" \
    -I "$HOST_DIR" \
    -Xcc -fmodule-map-file="$TYPED_PROJECTION_OUTPUT/module.modulemap" \
    "$TYPED_PROJECTION_OUTPUT/NetLift.swift" \
    "$HOST_DIR/TypedReplySwiftHost.swift" \
    "$HOST_OUTPUT/typed_reply_host.o" \
    -L "$HOST_NATIVE" -lnethost \
    -Xlinker -rpath -Xlinker "$HOST_NATIVE" \
    -ldl -lpthread \
    -o "$HOST_OUTPUT/netlift-typed-reply-host"

"$HOST_OUTPUT/netlift-typed-reply-host" \
    "$DOTNET_ROOT" \
    "$TYPED_MANAGED_OUTPUT/NetLift.NativeHost.TypedReply.Managed.dll" \
    "$TYPED_MANAGED_OUTPUT/NetLift.NativeHost.TypedReply.Managed.runtimeconfig.json"
