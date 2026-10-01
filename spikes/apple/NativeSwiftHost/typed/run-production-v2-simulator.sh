#!/bin/sh
set -eu

if [ "$#" -ne 1 ]; then
    echo "usage: run-production-v2-simulator.sh SIMULATOR_UDID" >&2
    exit 64
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../../../.." && pwd)
device=$1
export DEVELOPER_DIR=${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}
project="$script_dir/NativeSwiftHostTyped.xcodeproj"
derived_data="$script_dir/.artifacts/production-v2/DerivedData"
app="$derived_data/Build/Products/Debug-iphonesimulator/NativeSwiftHostTyped.app"
bundle_id=org.netlift.nativeswifthost.typed.v2optin

if [ ! -d "$project" ]; then
    echo "The existing typed Xcode project is missing; refusing to regenerate the default project." >&2
    exit 66
fi
if ! xcrun simctl list devices available | grep -F "($device)" >/dev/null; then
    echo "The requested UDID is not an available iOS Simulator: $device" >&2
    exit 65
fi

sh "$script_dir/generate-typed-facade.sh"
xcodebuild -quiet \
    -project "$project" \
    -scheme NativeSwiftHostTyped \
    -configuration Debug \
    -sdk iphonesimulator \
    -destination "id=$device" \
    -derivedDataPath "$derived_data" \
    -xcconfig "$script_dir/ProductionV2OptIn.xcconfig" \
    CODE_SIGNING_ALLOWED=YES \
    CODE_SIGN_IDENTITY=- \
    GCC_PREPROCESSOR_DEFINITIONS='$(inherited) NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1' \
    SWIFT_ACTIVE_COMPILATION_CONDITIONS='$(inherited) NETLIFT_V2_PRODUCTION_PROOF' \
    PRODUCT_BUNDLE_IDENTIFIER="$bundle_id" \
    clean build

if ! xcrun simctl list devices booted | grep -F "($device)" >/dev/null; then
    xcrun simctl boot "$device"
fi
xcrun simctl bootstatus "$device" -b
xcrun simctl install "$device" "$app"
xcrun simctl terminate "$device" "$bundle_id" >/dev/null 2>&1 || true
xcrun simctl launch "$device" "$bundle_id"
bash "$repo_root/tools/inspect-mobile-package.sh" ios "$app" \
    NetLift.ManagedBridge.Typed.dll Orders.Business.dll
