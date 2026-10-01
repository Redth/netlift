#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACTS="$HOST_DIR/.artifacts/experiments/r2r-host-link-only"
ASSET_ROOT="$HOST_DIR/.artifacts/experiments/plainnet-r2r-asset-only/repro-single-composite"
FRAMEWORK_SOURCE="$ASSET_ROOT/obj/NetLift.ManagedBridge/release_ios-arm64/ios-arm64/r2rframework/NetLift.ManagedBridge.r2r.framework"
REGISTRY_OBJECT_SOURCE="$ASSET_ROOT/registration/r2r_modules.o"
DOTNET_EXE="$(command -v dotnet)"
DOTNET_ROOT="${DOTNET_ROOT:-$(cd "$(dirname "$DOTNET_EXE")" && pwd)}"
IOS_RUNTIME_PACK="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios-arm64.net11.0_26.5/26.5.12194-net11-rc.1/runtimes/ios-arm64/native"
FRAMEWORK_DIR="$ARTIFACTS/Frameworks"
REGISTRATION_DIR="$ARTIFACTS/registration"
NATIVE_DIR="$ARTIFACTS/native"
INCLUDE_DIR="$ARTIFACTS/include"
PROJECT="$SCRIPT_DIR/NativeSwiftHostR2RLinkOnly.xcodeproj"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
export DOTNET_ROOT

if [[ ! -f "$FRAMEWORK_SOURCE/NetLift.ManagedBridge.r2r" || ! -f "$REGISTRY_OBJECT_SOURCE" ]]; then
  bash "$HOST_DIR/build-asset-only-r2r.sh"
fi

for input in \
  "$FRAMEWORK_SOURCE/NetLift.ManagedBridge.r2r" \
  "$REGISTRY_OBJECT_SOURCE" \
  "$IOS_RUNTIME_PACK/libxamarin-dotnet-coreclr.a" \
  "$IOS_RUNTIME_PACK/xamarin/xamarin.h"; do
  if [[ ! -e "$input" ]]; then
    echo "Required build-only R2R input is missing: $input" >&2
    exit 2
  fi
done
if [[ ! -d "$DEVELOPER_DIR" ]]; then
  echo "Required Xcode developer directory not found: $DEVELOPER_DIR" >&2
  exit 2
fi

mkdir -p "$FRAMEWORK_DIR" "$REGISTRATION_DIR" "$NATIVE_DIR" "$INCLUDE_DIR"
ditto "$FRAMEWORK_SOURCE" "$FRAMEWORK_DIR/NetLift.ManagedBridge.r2r.framework"
cp "$REGISTRY_OBJECT_SOURCE" "$REGISTRATION_DIR/r2r_modules.o"
cp "$IOS_RUNTIME_PACK/libxamarin-dotnet-coreclr.a" "$NATIVE_DIR/"
ditto "$IOS_RUNTIME_PACK/xamarin" "$INCLUDE_DIR/xamarin"

xcodegen generate --spec "$SCRIPT_DIR/project.yml" --project "$SCRIPT_DIR"
xcodebuild \
  -project "$PROJECT" \
  -scheme NativeSwiftHostR2RLinkOnly \
  -configuration Release \
  -sdk iphoneos \
  -destination 'generic/platform=iOS' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY= \
  build 2>&1 | tee "$ARTIFACTS/xcodebuild.log"
