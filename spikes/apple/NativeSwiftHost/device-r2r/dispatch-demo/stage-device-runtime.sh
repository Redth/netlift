#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 2 || "$1" != "iphoneos" ]]; then
  echo "usage: stage-device-runtime.sh iphoneos APP_BUNDLE_RESOURCES" >&2
  exit 64
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_RESOURCES="$2"
DOTNET_EXE="$(command -v dotnet)"
SDK_BASE_PATH="$("$DOTNET_EXE" --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
if [[ -z "$SDK_BASE_PATH" ]]; then
  echo "Unable to determine the selected .NET SDK base path." >&2
  exit 66
fi
DOTNET_ROOT="${DOTNET_ROOT:-${SDK_BASE_PATH%/sdk/*}}"
RUNTIME_PACK_VERSION="26.5.12194-net11-rc.1"
IOS_SUFFIX="net11.0_26.5"
RUNTIME_VERSION="$(sed -n \
  's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
  "$SDK_BASE_PATH/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)"
NUGET_ROOT="$(dotnet nuget locals global-packages --list |
  sed -n 's/^global-packages: //p' | head -n 1)"
CORECLR_PACK="$NUGET_ROOT/microsoft.netcore.app.runtime.ios-arm64/$RUNTIME_VERSION/runtimes/ios-arm64"
IOS_RUNTIME="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios-arm64.$IOS_SUFFIX/$RUNTIME_PACK_VERSION/runtimes/ios-arm64"
IOS_SHARED="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios.$IOS_SUFFIX/$RUNTIME_PACK_VERSION/runtimes/ios"
MANAGED_OUTPUT="$SCRIPT_DIR/.artifacts/managed"
FRAMEWORK_SOURCE="$SCRIPT_DIR/.artifacts/Frameworks/NetLift.DispatchDemo.r2r.framework"
REGISTRY_SOURCE="$SCRIPT_DIR/.artifacts/registration/r2r_modules.o"
RUNTIME_ARCHIVE="$IOS_RUNTIME/native/libxamarin-dotnet-coreclr.a"

for input in \
  "$CORECLR_PACK/lib/net11.0/System.Private.CoreLib.dll" \
  "$CORECLR_PACK/native/libcoreclr.dylib" \
  "$IOS_RUNTIME/native/libxamarin-dotnet-coreclr.dylib" \
  "$IOS_SHARED/lib/net11.0/Microsoft.iOS.dll" \
  "$MANAGED_OUTPUT/NetLift.DispatchDemo.dll" \
  "$FRAMEWORK_SOURCE/NetLift.DispatchDemo.r2r" \
  "$REGISTRY_SOURCE" \
  "$RUNTIME_ARCHIVE"; do
  if [[ ! -e "$input" ]]; then
    echo "Required dispatch-demo staging input is missing: $input" >&2
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
  echo "The dispatch-demo no-JIT candidate unexpectedly stages libclrjit.dylib." >&2
  exit 1
fi
printf 'Staged dispatch-demo managed assets in %s; ReadyToRun is enabled by AppleManagedHostRuntime.\n' \
  "$APP_RESOURCES"
