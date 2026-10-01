#!/usr/bin/env bash
set -euo pipefail

repo_root="${NETLIFT_REPOSITORY_ROOT:?NETLIFT_REPOSITORY_ROOT is required}"
artifact_root="${NETLIFT_ASYNC_NATIVE_ARTIFACTS:?NETLIFT_ASYNC_NATIVE_ARTIFACTS is required}"
direction_file="$SRCROOT/contract-selection.txt"
output_dir="$DERIVED_FILE_DIR/AsyncV5"
module="$DERIVED_FILE_DIR/NetLiftV5Host.swiftmodule"
library="$DERIVED_FILE_DIR/libNetLiftV5Host.a"

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$status" -ne 0 ]]; then
        rm -rf "$output_dir"
        rm -f "$module" "$DERIVED_FILE_DIR/NetLiftV5Host.swiftdoc"
        rm -f "$DERIVED_FILE_DIR/NetLiftV5Host.swiftsourceinfo" "$library"
    fi
    exit "$status"
}
trap cleanup EXIT

direction="$(cat "$direction_file")"
case "$direction" in
    old|new) ;;
    *)
        echo "Unsupported async codec direction: $direction" >&2
        exit 1
        ;;
esac

python3 "$repo_root/spikes/build-dev-loop/V5AsyncNativeConsumer/stage-async-codec.py" \
    "$artifact_root/codec-lock.json" \
    "$direction" \
    swift \
    "$artifact_root/seed/$direction" \
    "$SRCROOT/NativeMain.$direction.swift" \
    "$output_dir"

swiftc \
    -target "$(uname -m)-apple-macos${MACOSX_DEPLOYMENT_TARGET}" \
    -emit-library \
    -static \
    -module-name NetLiftV5Host \
    "$SRCROOT/NetLiftV5AsyncHostStub.swift" \
    -emit-module-path "$module" \
    -o "$library"
echo "V5-ASYNC-XCODE-STUB-COMPILED direction=$direction"
