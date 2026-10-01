#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
APP="$ARTIFACTS/DerivedData/Build/Products/Release-iphonesimulator/OrdersR2RSimulatorProbe.app"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
bash "$SCRIPT_DIR/build-simulator-r2r-assets.sh"
xcodegen generate --spec "$SCRIPT_DIR/project-simulator.yml" --project "$SCRIPT_DIR"
xcodebuild \
  -project "$SCRIPT_DIR/OrdersR2RSimulatorProbe.xcodeproj" \
  -scheme OrdersR2RSimulatorProbe \
  -configuration Release \
  -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY= \
  build 2>&1 | tee "$ARTIFACTS/xcodebuild.log"

APP_EXECUTABLE="$APP/OrdersR2RSimulatorProbe"
R2R_FRAMEWORK="$APP/Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r"
if [[ ! -f "$APP_EXECUTABLE" || ! -f "$R2R_FRAMEWORK" ||
      -n "$(find "$APP" -type f -name 'Microsoft.NETCore.App.r2r.dylib' -print -quit)" ||
      -e "$APP/libclrjit.dylib" || -e "$APP/_CodeSignature" ]]; then
  echo "Unsigned Orders app-only simulator baseline failed package checks." >&2
  exit 1
fi
if ! grep -Fxq 'System.Private.CoreLib' "$ARTIFACTS/composite-strings.txt"; then
  echo "The app-only composite is missing System.Private.CoreLib." >&2
  exit 1
fi
file "$APP_EXECUTABLE" "$R2R_FRAMEWORK"
nm -gU "$APP_EXECUTABLE" | grep -E 'xamarin_get_native_code_data|xamarin_r2r_modules|xamarin_r2r_module_count'
printf 'Built app-only Orders R2R simulator baseline: %s\n' "$APP"
