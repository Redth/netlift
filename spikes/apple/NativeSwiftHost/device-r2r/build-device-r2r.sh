#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACTS="$HOST_DIR/.artifacts/experiments/device-r2r-host"
ASSET_ROOT="$HOST_DIR/.artifacts/experiments/plainnet-r2r-asset-only/repro-single-composite"
FRAMEWORK_SOURCE="$ASSET_ROOT/obj/NetLift.ManagedBridge/release_ios-arm64/ios-arm64/r2rframework/NetLift.ManagedBridge.r2r.framework"
DOTNET_EXE="$(command -v dotnet)"
SDK_BASE_PATH="$("$DOTNET_EXE" --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
if [[ -z "$SDK_BASE_PATH" ]]; then
  echo "Unable to determine the selected .NET SDK base path." >&2
  exit 66
fi
DOTNET_ROOT="${DOTNET_ROOT:-${SDK_BASE_PATH%/sdk/*}}"
IOS_RUNTIME_PACK="$DOTNET_ROOT/packs/Microsoft.iOS.Runtime.ios-arm64.net11.0_26.5/26.5.12194-net11-rc.1/runtimes/ios-arm64/native"
RUNTIME_VERSION="$(sed -n \
  's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
  "$SDK_BASE_PATH/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)"
NUGET_ROOT="$("$DOTNET_EXE" nuget locals global-packages --list |
  sed -n 's/^global-packages: //p' | head -n 1)"
CORECLR_PACK="$NUGET_ROOT/microsoft.netcore.app.runtime.ios-arm64/$RUNTIME_VERSION/runtimes/ios-arm64"
RUNTIME_R2R="$CORECLR_PACK/lib/net11.0/Microsoft.NETCore.App.r2r.dylib"
RUNTIME_ARCHIVE="$IOS_RUNTIME_PACK/libxamarin-dotnet-coreclr.a"
REGISTRATION_OBJECT="$ARTIFACTS/registration/r2r_modules.o"
RUNTIME_R2R_STAGED="$ARTIFACTS/native/Microsoft.NETCore.App.r2r.dylib"
APP_R2R_FRAMEWORK="$ARTIFACTS/Frameworks/NetLift.ManagedBridge.r2r.framework"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
export DOTNET_ROOT
if [[ ! -f "$FRAMEWORK_SOURCE/NetLift.ManagedBridge.r2r" ]]; then
  bash "$HOST_DIR/build-asset-only-r2r.sh"
fi
for input in \
  "$FRAMEWORK_SOURCE/NetLift.ManagedBridge.r2r" \
  "$RUNTIME_ARCHIVE" \
  "$RUNTIME_R2R"; do
  if [[ ! -e "$input" ]]; then
    echo "Required device R2R link input is missing: $input" >&2
    exit 66
  fi
done
if [[ ! -d "$DEVELOPER_DIR" ]]; then
  echo "Required Xcode developer directory not found: $DEVELOPER_DIR" >&2
  exit 66
fi

mkdir -p "$ARTIFACTS/Frameworks" "$ARTIFACTS/registration" "$ARTIFACTS/native"
ditto "$FRAMEWORK_SOURCE" "$APP_R2R_FRAMEWORK"
cp "$RUNTIME_ARCHIVE" "$ARTIFACTS/native/"
cp "$RUNTIME_R2R" "$RUNTIME_R2R_STAGED"
xcrun --sdk iphoneos clang++ \
  -target arm64-apple-ios15.0 \
  -isysroot "$(xcrun --sdk iphoneos --show-sdk-path)" \
  -I "$IOS_RUNTIME_PACK" \
  -c "$SCRIPT_DIR/OrdersDeviceR2RModuleRegistry.mm" \
  -o "$REGISTRATION_OBJECT"

nm -gU "$RUNTIME_R2R_STAGED" > "$ARTIFACTS/native/runtime-r2r-symbols.txt"
nm -gU "$APP_R2R_FRAMEWORK/NetLift.ManagedBridge.r2r" \
  > "$ARTIFACTS/native/app-r2r-symbols.txt"
if ! grep -Eq '[[:space:]]_RTR_HEADER$' "$ARTIFACTS/native/runtime-r2r-symbols.txt" ||
   ! grep -Fq '_RTR_HEADER_NetLift_ManagedBridge_r2r_o' "$ARTIFACTS/native/app-r2r-symbols.txt"; then
  echo "Runtime or Orders app-composite R2R header symbol is missing." >&2
  exit 1
fi
for owner in Microsoft.NETCore.App.r2r.dylib NetLift.ManagedBridge.r2r.dylib; do
  if [[ "$(strings "$REGISTRATION_OBJECT" | grep -Fc "$owner")" -ne 1 ]]; then
    echo "Dual-image registry must contain exactly one entry for $owner." >&2
    exit 1
  fi
done

xcodegen generate --spec "$SCRIPT_DIR/project.yml" --project "$SCRIPT_DIR"
xcodebuild \
  -project "$SCRIPT_DIR/NativeSwiftHostDeviceR2R.xcodeproj" \
  -scheme NativeSwiftHostDeviceR2R \
  -configuration Release \
  -sdk iphoneos \
  -destination 'generic/platform=iOS' \
  -derivedDataPath "$ARTIFACTS/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY= \
  build 2>&1 | tee "$ARTIFACTS/xcodebuild.log"

APP="$ARTIFACTS/DerivedData/Build/Products/Release-iphoneos/NativeSwiftHostDeviceR2R.app"
APP_EXECUTABLE="$APP/NativeSwiftHostDeviceR2R"
R2R_FRAMEWORK="$APP/Frameworks/NetLift.ManagedBridge.r2r.framework/NetLift.ManagedBridge.r2r"
RUNTIME_R2R_APP="$APP/Frameworks/Microsoft.NETCore.App.r2r.dylib"
if [[ ! -f "$APP_EXECUTABLE" || ! -f "$R2R_FRAMEWORK" || ! -f "$RUNTIME_R2R_APP" ]]; then
  echo "The device build did not produce the executable and both R2R images." >&2
  exit 1
fi

framework_count="$(find "$APP/Frameworks" -maxdepth 1 -type d -name 'NetLift.ManagedBridge.r2r.framework' | wc -l | tr -d ' ')"
dll_count="$(find "$APP" -maxdepth 1 -type f -name '*.dll' | wc -l | tr -d ' ')"
dylib_count="$(find "$APP" -maxdepth 1 -type f -name 'lib*.dylib' | wc -l | tr -d ' ')"
if [[ "$framework_count" -ne 1 || "$dll_count" -ne 185 || "$dylib_count" -ne 9 ]]; then
  echo "Unexpected no-JIT device app contents: $framework_count R2R frameworks, $dll_count DLLs, $dylib_count dylibs." >&2
  exit 1
fi
runtime_r2r_count="$(find "$APP" -type f -name 'Microsoft.NETCore.App.r2r.dylib' | wc -l | tr -d ' ')"
if [[ "$runtime_r2r_count" -ne 1 ]]; then
  echo "Expected exactly one embedded Microsoft.NETCore.App.r2r.dylib; found $runtime_r2r_count." >&2
  exit 1
fi
if [[ -n "$(find "$APP" -type f -name 'libclrjit.dylib' -print -quit)" ]]; then
  echo "The no-JIT device app unexpectedly contains libclrjit.dylib." >&2
  exit 1
fi
if [[ -e "$APP/_CodeSignature" ]]; then
  echo "The build-only app unexpectedly contains code signatures." >&2
  exit 1
fi

printf 'Unsigned device-RID app: %s\n' "$APP"
printf 'Managed assemblies: %s; native runtime dylibs: %s; Orders app R2R frameworks: %s; runtime R2R dylibs: %s\n' \
  "$dll_count" "$dylib_count" "$framework_count" "$runtime_r2r_count"
printf 'JIT backend: intentionally not packaged (ReadyToRun is enabled by the native host).\n'
file "$APP_EXECUTABLE" "$R2R_FRAMEWORK" "$RUNTIME_R2R_APP"
nm -gU "$APP_EXECUTABLE" | grep -E 'xamarin_get_native_code_data|xamarin_r2r_modules|xamarin_r2r_module_count'
nm -gU "$R2R_FRAMEWORK" | grep -F '_RTR_HEADER_NetLift_ManagedBridge_r2r_o'
nm -gU "$RUNTIME_R2R_APP" | grep -E '_RTR_HEADER$'
strings "$REGISTRATION_OBJECT" | grep -E 'Microsoft.NETCore.App.r2r.dylib|NetLift.ManagedBridge.r2r.dylib'
otool -L "$APP_EXECUTABLE" | grep -F 'NetLift.ManagedBridge.r2r.framework'
otool -L "$APP_EXECUTABLE" | grep -F 'Microsoft.NETCore.App.r2r.dylib'
