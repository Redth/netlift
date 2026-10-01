#!/bin/sh
set -eu

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
DOTNET_ROOT=${DOTNET_ROOT:-"$HOME/Library/Application Support/dotnetup/dotnet"}
RUNTIME_VERSION=11.0.0-rc.1.26425.128
RUNTIME_DIR="$DOTNET_ROOT/shared/Microsoft.NETCore.App/$RUNTIME_VERSION"
MANAGED_DIR="$HERE/Managed/bin/Release/net11.0"

if [ ! -f "$RUNTIME_DIR/libcoreclr.dylib" ]; then
    printf 'CoreCLR not found at %s\n' "$RUNTIME_DIR" >&2
    exit 1
fi

dotnet build "$HERE/Managed/NetLift.AndroidRuntime.DelegateSmoke.csproj" \
    -c Release -v:minimal
clang++ -std=c++17 -O2 "$HERE/native/host.cpp" -ldl -o "$HERE/native/host"
"$HERE/native/host" "$RUNTIME_DIR" "$MANAGED_DIR"
