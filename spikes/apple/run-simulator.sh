#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project="$script_dir/AppleCoreClrHost/AppleCoreClrHost.csproj"
device=${1:-3510D274-4C09-4812-AA8F-47B0C5FB1AAE}
export DEVELOPER_DIR=${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}

if ! xcrun simctl list devices booted | grep -F "$device" >/dev/null; then
    xcrun simctl boot "$device"
fi
xcrun simctl bootstatus "$device" -b

dotnet restore "$project" -p:RuntimeIdentifier=iossimulator-arm64
dotnet build "$project" \
    -c Debug \
    -f net11.0-ios \
    -r iossimulator-arm64 \
    --no-restore

app="$script_dir/AppleCoreClrHost/bin/Debug/net11.0-ios/iossimulator-arm64/AppleCoreClrHost.app"
xcrun simctl install "$device" "$app"
xcrun simctl terminate "$device" org.netlift.applecoreclr >/dev/null 2>&1 || true
xcrun simctl launch "$device" org.netlift.applecoreclr
