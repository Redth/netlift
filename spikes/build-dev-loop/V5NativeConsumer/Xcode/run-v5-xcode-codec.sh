#!/usr/bin/env bash
set -euo pipefail

repo_root="${NETLIFT_REPOSITORY_ROOT:?NETLIFT_REPOSITORY_ROOT must point at the repository root}"
artifact_root="${NETLIFT_NATIVE_GRAPH_ARTIFACTS:?NETLIFT_NATIVE_GRAPH_ARTIFACTS must point at the isolated artifact root}"
selected="$SRCROOT/contract-selection.txt"
output_dir="$DERIVED_FILE_DIR/NetLiftSchemaV5"
host_module="$DERIVED_FILE_DIR/NetLiftV5Host.swiftmodule"
host_library="$DERIVED_FILE_DIR/libNetLiftV5Host.a"
temporary=""

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$status" -ne 0 ]]; then
        rm -rf "$output_dir"
        rm -f "$host_module" "$DERIVED_FILE_DIR/NetLiftV5Host.swiftdoc"
        rm -f "$DERIVED_FILE_DIR/NetLiftV5Host.swiftsourceinfo" "$host_library"
    fi
    if [[ -n "$temporary" ]]; then
        rm -rf "$temporary"
    fi
    exit "$status"
}
trap cleanup EXIT

direction="$(cat "$selected")"
case "$direction" in
    old|new) ;;
    *)
        echo "Unsupported schema-v5 contract selection: $direction" >&2
        exit 1
        ;;
esac

swiftc \
    -target "$(uname -m)-apple-macos${MACOSX_DEPLOYMENT_TARGET}" \
    -emit-library \
    -static \
    -module-name NetLiftV5Host \
    "$SRCROOT/NetLiftV5HostTypecheckStub.swift" \
    -emit-module-path "$host_module" \
    -o "$host_library"

mkdir -p "$artifact_root/staging"
temporary="$(mktemp -d "$artifact_root/staging/xcode-consumer.XXXXXX")"
consumer="$SRCROOT/NativeMain.$direction.swift"
staged_consumer="$output_dir/NativeMain.swift"

bash "$repo_root/spikes/build-dev-loop/V5NativeConsumer/stage-v5-codec.sh" \
    "$direction" swift "$output_dir" "xcode-$CONFIGURATION"

mkdir -p "$temporary"
cp "$consumer" "$temporary/NativeMain.swift"
if [[ -f "$staged_consumer" ]] && cmp -s "$temporary/NativeMain.swift" "$staged_consumer"; then
    :
else
    mv "$temporary/NativeMain.swift" "$staged_consumer"
fi

if [[ ! -s "$output_dir/NetLift.swift" ||
      ! -s "$output_dir/codec-provenance.json" ||
      ! -s "$staged_consumer" ]]; then
    echo "Native v5 Swift source staging is incomplete." >&2
    exit 1
fi
echo "XCODE-V5-CODEC: direction=$direction"
