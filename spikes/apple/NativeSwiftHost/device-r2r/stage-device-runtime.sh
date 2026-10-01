#!/bin/bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  echo "usage: stage-device-runtime.sh PLATFORM_NAME APP_BUNDLE_RESOURCES" >&2
  exit 64
fi

platform_name="$1"
bundle_resources="$2"
if [[ "$platform_name" != "iphoneos" ]]; then
  echo "Device R2R staging requires PLATFORM_NAME=iphoneos; received $platform_name." >&2
  exit 65
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACTS="$HOST_DIR/.artifacts/experiments/device-r2r-host"
RUNTIME_R2R="$ARTIFACTS/native/Microsoft.NETCore.App.r2r.dylib"
MANAGED_PROJECT="$HOST_DIR/managed/NetLift.ManagedBridge.csproj"
MANAGED_OUTPUT="$ARTIFACTS/managed/device-Release"
DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
SDK_BASE_PATH="$(dotnet --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
if [[ -z "$SDK_BASE_PATH" ]]; then
  echo "Unable to determine the selected .NET SDK base path." >&2
  exit 66
fi
DOTNET_ROOT="${DOTNET_ROOT:-${SDK_BASE_PATH%/sdk/*}}"
SDK_VERSION="$(dotnet --version)"
SDK_BAND="$(printf '%s\n' "$SDK_VERSION" | sed -E 's/(\.[0-9]+){2}$//')"
RUNTIME_PACK_VERSION="$(sed -n \
  's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
  "$SDK_BASE_PATH/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)"
IOS_MANIFEST_ROOT="$DOTNET_ROOT/sdk-manifests/$SDK_BAND/microsoft.net.sdk.ios"
IOS_MANIFEST="$(find "$IOS_MANIFEST_ROOT" -mindepth 2 -maxdepth 2 \
  -type f -name WorkloadManifest.json -print 2>/dev/null | sort -V | tail -n 1)"
if [[ -z "$RUNTIME_PACK_VERSION" || -z "$IOS_MANIFEST" ]]; then
  echo "Unable to resolve the selected .NET runtime or iOS workload manifest." >&2
  exit 66
fi

IOS_PACK_SUFFIX=net11.0_26.5
IOS_RUNTIME_PACK_NAME="Microsoft.iOS.Runtime.ios-arm64.$IOS_PACK_SUFFIX"
IOS_SHARED_PACK_NAME="Microsoft.iOS.Runtime.ios.$IOS_PACK_SUFFIX"
IOS_PACK_VERSION="$(sed -n \
  "/\"$IOS_RUNTIME_PACK_NAME\"[[:space:]]*:/,/}/ s/.*\"version\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" \
  "$IOS_MANIFEST" | head -n 1)"
if [[ -z "$IOS_PACK_VERSION" ]]; then
  echo "The selected iOS workload manifest does not declare $IOS_RUNTIME_PACK_NAME." >&2
  exit 66
fi

NUGET_ROOT="$(dotnet nuget locals global-packages --list |
  sed -n 's/^global-packages: //p' | head -n 1)"
CORECLR_PACK="$NUGET_ROOT/microsoft.netcore.app.runtime.ios-arm64/$RUNTIME_PACK_VERSION/runtimes/ios-arm64"
IOS_RUNTIME_PACK="$DOTNET_ROOT/packs/$IOS_RUNTIME_PACK_NAME/$IOS_PACK_VERSION/runtimes/ios-arm64"
IOS_SHARED_PACK="$DOTNET_ROOT/packs/$IOS_SHARED_PACK_NAME/$IOS_PACK_VERSION/runtimes/ios"
XAMARIN_SHIM="$IOS_RUNTIME_PACK/native/libxamarin-dotnet-coreclr.dylib"
IOS_BINDINGS="$IOS_SHARED_PACK/lib/net11.0/Microsoft.iOS.dll"

for input in \
  "$CORECLR_PACK/lib/net11.0/System.Private.CoreLib.dll" \
  "$CORECLR_PACK/native/libcoreclr.dylib" \
  "$CORECLR_PACK/native/libclrjit.dylib" \
  "$XAMARIN_SHIM" \
  "$IOS_BINDINGS" \
  "$MANAGED_PROJECT"; do
  if [[ ! -e "$input" ]]; then
    echo "Required device runtime input is missing: $input" >&2
    exit 66
  fi
done
if [[ ! -f "$RUNTIME_R2R" ]]; then
  echo "Dual-image runtime R2R image was not staged by build-device-r2r.sh: $RUNTIME_R2R" >&2
  exit 66
fi

export DOTNET_ROOT
export DEVELOPER_DIR
mkdir -p "$bundle_resources" "$MANAGED_OUTPUT"
rm -f "$bundle_resources/libclrjit.dylib" \
  "$bundle_resources/Microsoft.NETCore.App.r2r.dylib"

run_dotnet() {
  env -i \
    PATH="$PATH" \
    HOME="$HOME" \
    TMPDIR="${TMPDIR:-/tmp}" \
    DOTNET_ROOT="$DOTNET_ROOT" \
    DEVELOPER_DIR="$DEVELOPER_DIR" \
    dotnet "$@"
}

run_dotnet build "$MANAGED_PROJECT" \
  -c Release \
  -f net11.0 \
  --output "$MANAGED_OUTPUT" \
  --nologo \
  -v:minimal

for source in "$CORECLR_PACK"/lib/net11.0/*.dll; do
  [[ -f "$source" ]] && cp "$source" "$bundle_resources/"
done
cp "$IOS_BINDINGS" "$bundle_resources/"
for source in "$CORECLR_PACK"/native/lib*.dylib; do
  [[ -f "$source" ]] || continue
  if [[ "$(basename "$source")" != "libclrjit.dylib" ]]; then
    cp "$source" "$bundle_resources/"
  fi
done
cp "$XAMARIN_SHIM" "$bundle_resources/"
for assembly in NetLift.ManagedBridge NetLift.Contracts Orders.Business; do
  source="$MANAGED_OUTPUT/$assembly.dll"
  if [[ ! -f "$source" ]]; then
    echo "Managed bridge output is missing: $source" >&2
    exit 1
  fi
  cp "$source" "$bundle_resources/"
done

dll_count="$(find "$bundle_resources" -maxdepth 1 -type f -name '*.dll' | wc -l | tr -d ' ')"
dylib_count="$(find "$bundle_resources" -maxdepth 1 -type f -name 'lib*.dylib' | wc -l | tr -d ' ')"
if [[ "$dll_count" -ne 185 || "$dylib_count" -ne 9 ]]; then
  echo "Unexpected no-JIT device asset closure: $dll_count DLLs and $dylib_count dylibs (expected 185 and 9)." >&2
  exit 1
fi
if [[ -e "$bundle_resources/libclrjit.dylib" ||
      -e "$bundle_resources/Microsoft.NETCore.App.r2r.dylib" ]]; then
  echo "The no-JIT device resource closure must not contain JIT or duplicate root runtime R2R." >&2
  exit 1
fi

printf 'Staged ios-arm64 CoreCLR/iOS packs and three plain net11.0 bridge assemblies in %s (%s DLLs, %s dylibs; libclrjit.dylib excluded).\n' \
  "$bundle_resources" "$dll_count" "$dylib_count"
