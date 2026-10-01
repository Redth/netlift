#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
DUAL_IMAGE_ARTIFACTS="$SCRIPT_DIR/../.artifacts/simulator"
PROJECT="$SCRIPT_DIR/OrdersR2RManagedExecution.xcodeproj"
DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"

export DEVELOPER_DIR
for input in \
  "$DUAL_IMAGE_ARTIFACTS/Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r" \
  "$DUAL_IMAGE_ARTIFACTS/registration/r2r_modules.o" \
  "$DUAL_IMAGE_ARTIFACTS/native/libxamarin-dotnet-coreclr.a" \
  "$DUAL_IMAGE_ARTIFACTS/native/Microsoft.NETCore.App.r2r.dylib" \
  "$DUAL_IMAGE_ARTIFACTS/managed/NetLift.ManagedBridge.dll" \
  "$DUAL_IMAGE_ARTIFACTS/managed/Orders.Business.dll"; do
  if [[ ! -e "$input" ]]; then
    echo "Required existing dual-image input is missing: $input" >&2
    echo "Build/review the dual-image startup targets before this isolated proof." >&2
    exit 66
  fi
done

mkdir -p "$ARTIFACTS"
xcodegen generate --spec "$SCRIPT_DIR/project.yml" --project "$SCRIPT_DIR"

for scheme in OrdersR2RManagedAppOwnerProbe OrdersR2RManagedRuntimeOwnerProbe; do
  xcodebuild \
    -project "$PROJECT" \
    -scheme "$scheme" \
    -configuration Release \
    -sdk iphonesimulator \
    -destination 'generic/platform=iOS Simulator' \
    -derivedDataPath "$ARTIFACTS/DerivedData" \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY= \
    build 2>&1 | tee "$ARTIFACTS/xcodebuild-$scheme.log"
done

for target in OrdersR2RManagedAppOwnerProbe OrdersR2RManagedRuntimeOwnerProbe; do
  app="$ARTIFACTS/DerivedData/Build/Products/Release-iphonesimulator/$target.app"
  if [[ ! -f "$app/$target" ||
        ! -f "$app/Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r" ||
        ! -f "$app/Frameworks/Microsoft.NETCore.App.r2r.dylib" ||
        -e "$app/libclrjit.dylib" ]]; then
    echo "Managed execution simulator package failed its R2R/no-JIT checks: $app" >&2
    exit 1
  fi
  printf '%s\n' "$app/$target"
  file "$app/$target"
done
