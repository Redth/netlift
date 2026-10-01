#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
generated_root="${NETLIFT_V5_HOST_CODEC_ROOT:-$repo_root/tests/NetLift.Tests/bin/Debug/net11.0/projection-schema-v5-host-codecs}"
host_module_dir="$generated_root/test-host-module"
mkdir -p "$host_module_dir"

swiftc -swift-version 6 -emit-library -emit-module \
    -module-name NetLiftV5Host \
    -emit-module-path "$host_module_dir/NetLiftV5Host.swiftmodule" \
    tests/NetLift.Tests/SwiftSchemaV5HostModuleStub.swift \
    -o "$host_module_dir/libNetLiftV5Host.dylib"

for direction in old new; do
    define=()
    if [[ "$direction" == "old" ]]; then
        define=(-D SCHEMA_V5_HOST_OLD)
    else
        define=(-D SCHEMA_V5_HOST_NEW)
    fi
    swiftc -swift-version 6 -parse-as-library "${define[@]}" \
        -I "$host_module_dir" \
        "$generated_root/$direction/NetLift.swift" \
        tests/NetLift.Tests/SwiftSchemaV5HostCodecSmoke.swift \
        -L "$host_module_dir" -lNetLiftV5Host \
        -Xlinker -rpath -Xlinker "$host_module_dir" \
        -o "$generated_root/$direction/SwiftSchemaV5HostCodecSmoke"
    "$generated_root/$direction/SwiftSchemaV5HostCodecSmoke" "$generated_root/fixtures"
done

if [[ -n "${NETLIFT_GRADLE:-}" ]]; then
    gradle_bin="$NETLIFT_GRADLE"
elif command -v gradle >/dev/null 2>&1; then
    gradle_bin="gradle"
else
    gradle_bin="$repo_root/spikes/android/NetLift.Compose/gradlew"
fi
for direction in old new; do
    "$gradle_bin" -p tests/NetLift.KotlinSchemaV5HostCodecCompile \
        -PnetliftV5Projection="$direction" \
        -PnetliftV5HostCodecRoot="$generated_root" \
        run --offline --no-daemon --rerun-tasks --console=plain
done
