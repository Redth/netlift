#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
runtime_id=${1:-iossimulator-arm64}

case "$runtime_id" in
    iossimulator-arm64)
        sdk_name=iphonesimulator
        target=arm64-apple-ios15.0-simulator
        ;;
    ios-arm64)
        sdk_name=iphoneos
        target=arm64-apple-ios15.0
        ;;
    *)
        echo "Unsupported RuntimeIdentifier for the SwiftUI spike: $runtime_id" >&2
        exit 2
        ;;
esac

sdk_path=$(xcrun --sdk "$sdk_name" --show-sdk-path)
output="$script_dir/obj/SwiftUIBridge/libNetLiftSwiftUI.a"
mkdir -p "$(dirname -- "$output")"

swiftc \
    -target "$target" \
    -sdk "$sdk_path" \
    -parse-as-library \
    -emit-library \
    -static \
    -module-name NetLiftSwiftUI \
    "$script_dir/SwiftUIBridge.swift" \
    -o "$output"
