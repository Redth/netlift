#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts/simulator"
APP="$ARTIFACTS/DerivedData/Build/Products/Release-iphonesimulator/DispatchDemoSimulator.app"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
bash "$SCRIPT_DIR/build-simulator-r2r-assets.sh"
xcodegen generate --spec "$SCRIPT_DIR/project-simulator.yml" --project "$SCRIPT_DIR"
xcodebuild \
  -project "$SCRIPT_DIR/DispatchDemoSimulator.xcodeproj" \
  -scheme DispatchDemoSimulator \
  -configuration Release \
  -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY= \
  build 2>&1 | tee "$ARTIFACTS/xcodebuild.log"

if [[ ! -f "$APP/DispatchDemoSimulator" ||
      ! -f "$APP/Frameworks/NetLift.DispatchDemo.r2r.framework/NetLift.DispatchDemo.r2r" ||
      ! -f "$APP/Frameworks/Microsoft.NETCore.App.r2r.dylib" ||
      -e "$APP/libclrjit.dylib" ||
      -e "$APP/_CodeSignature" ]]; then
  echo "Unsigned simulator package failed its output/JIT/signature checks." >&2
  exit 1
fi
file "$APP/DispatchDemoSimulator" "$APP/Frameworks/NetLift.DispatchDemo.r2r.framework/NetLift.DispatchDemo.r2r"
nm -gU "$APP/DispatchDemoSimulator" | grep -E 'xamarin_get_native_code_data|xamarin_r2r_modules|xamarin_r2r_module_count'
otool -L "$APP/DispatchDemoSimulator" | grep -F 'NetLift.DispatchDemo.r2r.framework'
otool -L "$APP/DispatchDemoSimulator" | grep -F '@rpath/Microsoft.NETCore.App.r2r.dylib'
