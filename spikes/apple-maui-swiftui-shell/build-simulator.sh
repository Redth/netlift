#!/bin/sh
set -eu

: "${APPLE_SPIKE_ARTIFACTS:?Set APPLE_SPIKE_ARTIFACTS to an isolated session artifact directory}"

PROJECT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
NATIVE_OUTPUT="$APPLE_SPIKE_ARTIFACTS/native"
DOTNET_OUTPUT="$APPLE_SPIKE_ARTIFACTS/dotnet"
LOG_OUTPUT="$APPLE_SPIKE_ARTIFACTS/logs"
SWIFT_LIBRARY="$NATIVE_OUTPUT/libNetLiftSwiftUIShell.a"

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$APPLE_SPIKE_ARTIFACTS/home}"
export TMPDIR="${TMPDIR:-$APPLE_SPIKE_ARTIFACTS/tmp}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$APPLE_SPIKE_ARTIFACTS/nuget/packages}"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"

mkdir -p "$NATIVE_OUTPUT" "$DOTNET_OUTPUT" "$LOG_OUTPUT" "$TMPDIR" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

SIMULATOR_SDK=$(xcrun --sdk iphonesimulator --show-sdk-path)
xcrun --sdk iphonesimulator swiftc \
	-sdk "$SIMULATOR_SDK" \
	-target arm64-apple-ios26.5-simulator \
	-module-name NetLiftSwiftUIShell \
	-emit-library \
	-static \
	"$PROJECT_DIR/native/SwiftUIShell.swift" \
	-o "$SWIFT_LIBRARY" \
	> "$LOG_OUTPUT/swift-build.log" 2>&1

dotnet build "$PROJECT_DIR/NetLift.MauiSwiftUIShell.csproj" \
	--framework net11.0-ios \
	--configuration Debug \
	--runtime iossimulator-arm64 \
	--artifacts-path "$DOTNET_OUTPUT" \
	-p:RestoreSources=/Users/redth/.nuget/packages \
	-p:AppleNativeLibrary="$SWIFT_LIBRARY" \
	> "$LOG_OUTPUT/dotnet-build.log" 2>&1

tail -n 30 "$LOG_OUTPUT/swift-build.log"
tail -n 40 "$LOG_OUTPUT/dotnet-build.log"
