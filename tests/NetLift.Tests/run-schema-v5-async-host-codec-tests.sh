#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
generated_root="${NETLIFT_ASYNC_V5_HOST_CODEC_ROOT:-$repo_root/tests/NetLift.Tests/bin/Debug/net11.0/projection-schema-v5-async-host-codecs}"
projection_dll="${NETLIFT_PROJECTION_DLL:-$repo_root/src/NetLift.Projection/bin/Debug/net11.0/NetLift.Projection.dll}"
host_module_dir="$generated_root/test-host-module"
mkdir -p "$host_module_dir"

swiftc -swift-version 6 -emit-library -emit-module \
    -module-name NetLiftV5Host \
    -emit-module-path "$host_module_dir/NetLiftV5Host.swiftmodule" \
    tests/NetLift.Tests/SwiftSchemaV5AsyncHostModuleStub.swift \
    -o "$host_module_dir/libNetLiftV5Host.dylib"

for projection in old new utf8; do
    define=()
    case "$projection" in
        old) define=(-D SCHEMA_ASYNC_OLD) ;;
        new) define=(-D SCHEMA_ASYNC_NEW) ;;
        utf8) define=(-D SCHEMA_ASYNC_UTF8) ;;
    esac
    swiftc -swift-version 6 -parse-as-library "${define[@]}" \
        -I "$host_module_dir" \
        "$generated_root/$projection/NetLift.swift" \
        tests/NetLift.Tests/SwiftSchemaV5AsyncHostCodecSmoke.swift \
        -L "$host_module_dir" -lNetLiftV5Host \
        -Xlinker -rpath -Xlinker "$host_module_dir" \
        -o "$generated_root/$projection/SwiftSchemaV5AsyncHostCodecSmoke"
    "$generated_root/$projection/SwiftSchemaV5AsyncHostCodecSmoke" "$generated_root/fixtures"
done

dotnet "$projection_dll" \
    "$generated_root/fixtures/new-contract.g.cs" \
    "$generated_root/cli-new" \
    --emit-schema-v5-async-host-codecs
cmp "$generated_root/new/NetLift.swift" "$generated_root/cli-new/NetLift.swift"
cmp "$generated_root/new/NetLift.kt" "$generated_root/cli-new/NetLift.kt"

if [[ -n "${NETLIFT_GRADLE:-}" ]]; then
    gradle_bin="$NETLIFT_GRADLE"
elif command -v gradle >/dev/null 2>&1; then
    gradle_bin="gradle"
else
    gradle_bin="$repo_root/spikes/android/NetLift.Compose/gradlew"
fi
for projection in old new utf8; do
    "$gradle_bin" -p tests/NetLift.KotlinSchemaV5AsyncHostCodecCompile \
        -PnetliftAsyncV5Projection="$projection" \
        -PnetliftAsyncV5HostCodecRoot="$generated_root" \
        -PnetliftAsyncV5GradleBuildDir="$generated_root/gradle/$projection/build" \
        --project-cache-dir "$generated_root/gradle/$projection/cache" \
        run --offline --no-daemon --rerun-tasks --console=plain
done
