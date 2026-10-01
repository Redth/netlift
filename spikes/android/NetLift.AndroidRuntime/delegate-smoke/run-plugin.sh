#!/bin/sh
set -eu

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PROJECT_ROOT=$(CDPATH= cd -- "$HERE/.." && pwd)
DOTNET_ROOT=${DOTNET_ROOT:-"$HOME/Library/Application Support/dotnetup/dotnet"}
RUNTIME_VERSION=11.0.0-rc.1.26425.128
RUNTIME_DIR="$DOTNET_ROOT/shared/Microsoft.NETCore.App/$RUNTIME_VERSION"
MANAGED_DIR="$HERE/plugin-managed"

if [ ! -f "$RUNTIME_DIR/libcoreclr.dylib" ]; then
    printf 'CoreCLR not found at %s\n' "$RUNTIME_DIR" >&2
    exit 1
fi

dotnet publish \
    "$PROJECT_ROOT/sample/managed/NetLift.AndroidRuntime.StubContract.csproj" \
    -c Release -o "$MANAGED_DIR" -v:minimal
rm -f "$MANAGED_DIR/NetLift.AndroidRuntime.DelegateSmoke.dll"
clang++ -std=c++17 -O2 "$HERE/native/plugin-host.cpp" -ldl -o "$HERE/native/plugin-host"
"$HERE/native/plugin-host" "$RUNTIME_DIR" "$MANAGED_DIR"
for mismatch in version abi features hash; do
    "$HERE/native/plugin-host" "$RUNTIME_DIR" "$MANAGED_DIR" "--mismatch=$mismatch"
    "$HERE/native/plugin-host" "$RUNTIME_DIR" "$MANAGED_DIR" "--register-mismatch=$mismatch"
done
