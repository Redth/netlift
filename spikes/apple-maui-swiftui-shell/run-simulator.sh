#!/bin/sh
set -eu

: "${APPLE_SPIKE_ARTIFACTS:?Set APPLE_SPIKE_ARTIFACTS to the isolated session artifact directory}"
: "${SIMULATOR_UDID:?Set SIMULATOR_UDID to the dedicated iOS simulator UDID}"

PROJECT_NAME=NetLift.MauiSwiftUIShell
APP_PATH="$APPLE_SPIKE_ARTIFACTS/dotnet/bin/$PROJECT_NAME/debug_iossimulator-arm64/$PROJECT_NAME.app"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"

if [ ! -d "$APP_PATH" ]; then
	echo "Simulator app bundle does not exist: $APP_PATH" >&2
	exit 1
fi

xcrun simctl bootstatus "$SIMULATOR_UDID" -b
xcrun simctl install "$SIMULATOR_UDID" "$APP_PATH"
xcrun simctl launch "$SIMULATOR_UDID" com.companyname.netlift.mauiswiftuishell
