#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/.artifacts"
FRAMEWORKS="$ARTIFACTS/Frameworks"
REGISTRATION="$ARTIFACTS/registration/r2r_modules.o"
RUNTIME_ARCHIVE="$ARTIFACTS/native/libxamarin-dotnet-coreclr.a"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
bash "$SCRIPT_DIR/build-r2r-assets.sh"
if [[ ! -f "$REGISTRATION" || ! -f "$RUNTIME_ARCHIVE" ||
      ! -f "$FRAMEWORKS/NetLift.DispatchDemo.r2r.framework/NetLift.DispatchDemo.r2r" ]]; then
  echo "App-owned R2R framework, registry, or runtime archive was not produced." >&2
  exit 1
fi
xcodegen generate --spec "$SCRIPT_DIR/project.yml" --project "$SCRIPT_DIR"
xcodebuild \
  -project "$SCRIPT_DIR/DispatchDemo.xcodeproj" \
  -scheme DispatchDemo \
  -configuration Release \
  -sdk iphoneos \
  -destination 'generic/platform=iOS' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY= \
  build

APP="$ARTIFACTS/DerivedData/Build/Products/Release-iphoneos/DispatchDemo.app"
if [[ ! -f "$APP/DispatchDemo" ||
      ! -f "$APP/Frameworks/NetLift.DispatchDemo.r2r.framework/NetLift.DispatchDemo.r2r" ||
      ! -f "$APP/Frameworks/Microsoft.NETCore.App.r2r.dylib" ||
      -e "$APP/libclrjit.dylib" ||
      -e "$APP/_CodeSignature" ]]; then
  echo "Unsigned dispatch-demo device package failed its output/JIT/signature checks." >&2
  exit 1
fi
file "$APP/DispatchDemo" "$APP/Frameworks/NetLift.DispatchDemo.r2r.framework/NetLift.DispatchDemo.r2r"
nm -gU "$APP/DispatchDemo" | grep -E 'xamarin_get_native_code_data|xamarin_r2r_modules|xamarin_r2r_module_count'
otool -L "$APP/DispatchDemo" | grep -F 'NetLift.DispatchDemo.r2r.framework'
otool -L "$APP/DispatchDemo" | grep -F '@rpath/Microsoft.NETCore.App.r2r.dylib'
