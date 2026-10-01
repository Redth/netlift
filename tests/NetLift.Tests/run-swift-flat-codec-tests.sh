#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --nologo

output="$repo_root/tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-tests"
swiftc -swift-version 6 -parse-as-library \
    "$output/NetLift.swift" \
    tests/NetLift.Tests/SwiftFlatCodecSmoke.swift \
    -o "$output/SwiftFlatCodecSmoke"
"$output/SwiftFlatCodecSmoke" "$output"

collision="$repo_root/tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-flat-codec-collision-tests"
swiftc -swift-version 6 -parse-as-library -emit-module \
    -module-name SwiftFlatCodecCollision \
    "$collision/NetLift.swift" \
    -emit-module-path "$collision/SwiftFlatCodecCollision.swiftmodule"
