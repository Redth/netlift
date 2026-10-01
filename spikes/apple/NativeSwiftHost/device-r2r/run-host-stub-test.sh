#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$SCRIPT_DIR/AppleManagedHost"
OUTPUT_DIR="$SCRIPT_DIR/.artifacts/host-stub"
DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}"
MACOS_SDK="$(DEVELOPER_DIR="$DEVELOPER_DIR" xcrun --sdk macosx --show-sdk-path)"
mkdir -p "$OUTPUT_DIR"

bash "$SCRIPT_DIR/verify-host-contract.sh"

DEVELOPER_DIR="$DEVELOPER_DIR" swiftc \
  -swift-version 5 \
  -sdk "$MACOS_SDK" \
  -emit-library \
  -emit-module \
  -module-name AppleManagedHost \
  "$PACKAGE_DIR/Sources/AppleManagedHost/ManagedHostEventCenter.swift" \
  -o "$OUTPUT_DIR/libAppleManagedHost.dylib" \
  -emit-module-path "$OUTPUT_DIR/AppleManagedHost.swiftmodule"

DEVELOPER_DIR="$DEVELOPER_DIR" xcrun clang \
  -std=c11 \
  -pthread \
  -Wall -Wextra -Werror \
  -isysroot "$MACOS_SDK" \
  -c "$SCRIPT_DIR/host-stub/ManagedHostStub.c" \
  -o "$OUTPUT_DIR/ManagedHostStub.o"

DEVELOPER_DIR="$DEVELOPER_DIR" swiftc \
  -swift-version 5 \
  -sdk "$MACOS_SDK" \
  -I "$OUTPUT_DIR" \
  -L "$OUTPUT_DIR" \
  -lAppleManagedHost \
  "$SCRIPT_DIR/DeviceR2RManagedAdapter.swift" \
  "$SCRIPT_DIR/host-stub/ManagedHostStubTest.swift" \
  "$OUTPUT_DIR/ManagedHostStub.o" \
  -o "$OUTPUT_DIR/ManagedHostStubTest"

DYLD_LIBRARY_PATH="$OUTPUT_DIR${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}" \
  "$OUTPUT_DIR/ManagedHostStubTest"
printf 'Host-stub executable: %s\n' "$OUTPUT_DIR/ManagedHostStubTest"
