#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore

output="$repo_root/tests/NetLift.Tests/bin/Debug/net11.0/projection-schema-v5-codecs"
fixtures="$output/fixtures"

for direction in old new; do
    if [[ "$direction" == "old" ]]; then
        smoke_name="Old"
    else
        smoke_name="New"
    fi
    swiftc -swift-version 6 -parse-as-library \
        "$output/$direction/NetLift.swift" \
        tests/NetLift.Tests/SwiftSchemaV5CodecTestSupport.swift \
        "tests/NetLift.Tests/SwiftSchemaV5${smoke_name}CodecSmoke.swift" \
        -o "$output/$direction/SwiftSchemaV5CodecSmoke"
    "$output/$direction/SwiftSchemaV5CodecSmoke" "$fixtures"
done

if [[ -n "${NETLIFT_GRADLE:-}" ]]; then
    gradle_bin="$NETLIFT_GRADLE"
elif command -v gradle >/dev/null 2>&1; then
    gradle_bin="gradle"
else
    gradle_bin="$repo_root/spikes/android/NetLift.Compose/gradlew"
fi
for direction in old new; do
    "$gradle_bin" -p tests/NetLift.KotlinSchemaV5CodecCompile \
        -PnetliftV5Projection="$direction" \
        run --offline --no-daemon --rerun-tasks --console=plain
done
