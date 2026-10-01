#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 2 || "$1" != "iphonesimulator" ]]; then
  echo "usage: stage-simulator-runtime.sh iphonesimulator APP_BUNDLE_RESOURCES" >&2
  exit 64
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_RESOURCES="$2"
SDK_BASE_PATH="$(dotnet --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
if [[ -z "$SDK_BASE_PATH" ]]; then
  echo "Unable to determine the selected .NET SDK base path." >&2
  exit 66
fi
DOTNET_ROOT="${DOTNET_ROOT:-${SDK_BASE_PATH%/sdk/*}}"
RUNTIME_VERSION="$(sed -n \
  's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
  "$SDK_BASE_PATH/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)"
RUNTIME_PACK_VERSION="26.5.12194-net11-rc.1"
IOS_SUFFIX="net11.0_26.5"
NUGET_ROOT="$(dotnet nuget locals global-packages --list |
  sed -n 's/^global-packages: //p' | head -n 1)"
CORECLR_PACK="$NUGET_ROOT/microsoft.netcore.app.runtime.iossimulator-arm64/$RUNTIME_VERSION/runtimes/iossimulator-arm64"
IOS_RUNTIME="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.iossimulator-arm64.$IOS_SUFFIX/$RUNTIME_PACK_VERSION/runtimes/iossimulator-arm64"
IOS_SHARED="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios.$IOS_SUFFIX/$RUNTIME_PACK_VERSION/runtimes/ios"
MANAGED_OUTPUT="$SCRIPT_DIR/.artifacts/simulator/managed"
FRAMEWORK_SOURCE="$SCRIPT_DIR/.artifacts/simulator/Frameworks/NetLift.DispatchDemo.r2r.framework"
REGISTRY_SOURCE="$SCRIPT_DIR/.artifacts/simulator/registration/r2r_modules.o"

for input in \
  "$CORECLR_PACK/lib/net11.0/System.Private.CoreLib.dll" \
  "$CORECLR_PACK/native/libcoreclr.dylib" \
  "$IOS_RUNTIME/native/libxamarin-dotnet-coreclr.dylib" \
  "$IOS_SHARED/lib/net11.0/Microsoft.iOS.dll" \
  "$MANAGED_OUTPUT/NetLift.DispatchDemo.dll" \
  "$FRAMEWORK_SOURCE/NetLift.DispatchDemo.r2r" \
  "$REGISTRY_SOURCE"; do
  if [[ ! -e "$input" ]]; then
    echo "Required simulator staging input is missing: $input" >&2
    exit 66
  fi
done

mkdir -p "$APP_RESOURCES"
rm -f "$APP_RESOURCES/libclrjit.dylib"
for source in "$CORECLR_PACK"/lib/net11.0/*.dll; do
  [[ -f "$source" ]] && cp "$source" "$APP_RESOURCES/"
done
cp "$IOS_SHARED/lib/net11.0/Microsoft.iOS.dll" "$APP_RESOURCES/"
cp "$MANAGED_OUTPUT/NetLift.DispatchDemo.dll" "$APP_RESOURCES/"
for source in "$CORECLR_PACK"/native/lib*.dylib; do
  [[ -f "$source" && "$(basename "$source")" != "libclrjit.dylib" ]] &&
    cp "$source" "$APP_RESOURCES/"
done
cp "$IOS_RUNTIME/native/libxamarin-dotnet-coreclr.dylib" "$APP_RESOURCES/"

if [[ -e "$APP_RESOURCES/libclrjit.dylib" ]]; then
  echo "The simulator no-JIT candidate unexpectedly stages libclrjit.dylib." >&2
  exit 1
fi
printf 'Staged dispatch-demo simulator managed assets in %s.\n' "$APP_RESOURCES"
