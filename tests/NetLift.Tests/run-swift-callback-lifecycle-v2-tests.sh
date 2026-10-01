#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --no-restore

output="$repo_root/tests/NetLift.Tests/bin/Debug/net11.0/projection-swift-native-callback-v2"
swiftc -swift-version 6 -D DEBUG -typecheck \
    -I "$output/keyword" \
    -Xcc -fmodule-map-file="$output/keyword/module.modulemap" \
    "$output/keyword/NetLift.swift"
clang -std=c11 -Wall -Wextra -Werror \
    -pthread \
    -I "$output" \
    -c tests/NetLift.Tests/NetLiftCallbackLifecycleV2Fake.c \
    -o "$output/NetLiftCallbackLifecycleV2Fake.o"
swiftc -swift-version 6 -D DEBUG \
    -I "$output" \
    -Xcc -fmodule-map-file="$output/module.modulemap" \
    "$output/NetLift.swift" \
    tests/NetLift.Tests/SwiftCallbackLifecycleV2Smoke.swift \
    "$output/NetLiftCallbackLifecycleV2Fake.o" \
    -lpthread \
    -o "$output/SwiftCallbackLifecycleV2Smoke"
"$output/SwiftCallbackLifecycleV2Smoke"
