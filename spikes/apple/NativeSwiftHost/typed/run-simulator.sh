#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
device=${1:-3510D274-4C09-4812-AA8F-47B0C5FB1AAE}
configuration=${CONFIGURATION:-Debug}
export DEVELOPER_DIR=${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}
project="$script_dir/NativeSwiftHostTyped.xcodeproj"
derived_data=${DERIVED_DATA_PATH:-"$script_dir/.artifacts/DerivedData"}
app="$derived_data/Build/Products/$configuration-iphonesimulator/NativeSwiftHostTyped.app"

sh "$script_dir/generate-typed-facade.sh"
xcodegen generate --spec "$script_dir/project.yml" --project "$script_dir"
if ! xcrun simctl list devices booted | grep -F "$device" >/dev/null; then
    xcrun simctl boot "$device"
fi
xcrun simctl bootstatus "$device" -b
xcodebuild -quiet \
    -project "$project" \
    -scheme NativeSwiftHostTyped \
    -configuration "$configuration" \
    -sdk iphonesimulator \
    -destination "id=$device" \
    -derivedDataPath "$derived_data" \
    CODE_SIGNING_ALLOWED=YES \
    CODE_SIGN_IDENTITY=- \
    clean build
xcrun simctl install "$device" "$app"
xcrun simctl terminate "$device" org.netlift.nativeswifthost.typed >/dev/null 2>&1 || true
xcrun simctl launch "$device" org.netlift.nativeswifthost.typed
