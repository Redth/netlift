#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
PROJECT="$SCRIPT_DIR/OrdersR2RDualImageProbe.xcodeproj"
DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"

export DEVELOPER_DIR
bash "$SCRIPT_DIR/build-simulator-r2r-assets.sh"
xcodegen generate --spec "$SCRIPT_DIR/project-simulator.yml" --project "$SCRIPT_DIR"

xcodebuild \
  -project "$PROJECT" \
  -scheme OrdersR2RAppOwnerProbe \
  -configuration Release \
  -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY= \
  build 2>&1 | tee "$ARTIFACTS/xcodebuild-app-owner.log"

xcodebuild \
  -project "$PROJECT" \
  -scheme OrdersR2RRuntimeOwnerProbe \
  -configuration Release \
  -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY= \
  build 2>&1 | tee "$ARTIFACTS/xcodebuild-runtime-owner.log"

for app in \
  "$ARTIFACTS/DerivedData/Build/Products/Release-iphonesimulator/OrdersR2RAppOwnerProbe.app" \
  "$ARTIFACTS/DerivedData/Build/Products/Release-iphonesimulator/OrdersR2RRuntimeOwnerProbe.app"; do
  APP_EXECUTABLE="$app/$(basename "$app" .app)"
  APP_R2R="$app/Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r"
  RUNTIME_R2R="$app/Frameworks/Microsoft.NETCore.App.r2r.dylib"
  if [[ ! -f "$APP_EXECUTABLE" || ! -f "$APP_R2R" || ! -f "$RUNTIME_R2R" ||
        -e "$app/libclrjit.dylib" || -e "$app/_CodeSignature" ]]; then
    echo "Unsigned dual-image Orders simulator package failed its checks: $app" >&2
    exit 1
  fi
  if [[ "$(find "$app" -type f -name 'Microsoft.NETCore.App.r2r.dylib' | wc -l | tr -d ' ')" -ne 1 ||
        "$(find "$app" -type f -name 'NetLift.ManagedBridge.r2r' | wc -l | tr -d ' ')" -ne 1 ]]; then
    echo "Package must contain exactly one runtime and one Orders app R2R image: $app" >&2
    exit 1
  fi
  nm -gU "$APP_EXECUTABLE" > "$ARTIFACTS/$(basename "$app" .app)-symbols.txt"
  if ! grep -Fq '_xamarin_r2r_modules' "$ARTIFACTS/$(basename "$app" .app)-symbols.txt" ||
     ! grep -Fq '_xamarin_r2r_module_count' "$ARTIFACTS/$(basename "$app" .app)-symbols.txt"; then
    echo "Dual-image registry symbols are not linked into $APP_EXECUTABLE." >&2
    exit 1
  fi
  printf '%s\n' "$APP_EXECUTABLE"
  file "$APP_EXECUTABLE" "$APP_R2R" "$RUNTIME_R2R"
done
