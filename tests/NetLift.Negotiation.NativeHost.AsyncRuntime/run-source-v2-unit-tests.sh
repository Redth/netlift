#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"
APPLE_TYPED="$ROOT/spikes/apple/NativeSwiftHost/typed"

if [[ -z "${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS" && pwd)"
UNIT_OUTPUT="$ARTIFACT_ROOT/source-v2-unit"
mkdir -p "$UNIT_OUTPUT"

cc=${CC:-clang}
cxx=${CXX:-clang++}

"$cc" \
    -std=c11 \
    -Wall -Wextra -Werror \
    -pthread \
    -DNETLIFT_V2_TESTING \
    -I "$CORE_DIR" \
    -I "$APPLE_TYPED" \
    -I "$APPLE_TYPED/GeneratedTyped" \
    -I "$HOST_DIR" \
    "$CORE_DIR/async_v2_lifecycle.c" \
    "$HOST_DIR/source_v2_legacy_test_adapter.c" \
    "$APPLE_TYPED/tests/CallbackLifecycleV2Test.c" \
    -o "$UNIT_OUTPUT/CallbackLifecycleV2Test"
"$UNIT_OUTPUT/CallbackLifecycleV2Test"

"$cc" \
    -std=c11 \
    -Wall -Wextra -Werror \
    -pthread \
    -I "$CORE_DIR" \
    "$HOST_DIR/source_v2_accessor_test.c" \
    "$CORE_DIR/async_v2_lifecycle.c" \
    "$CORE_DIR/async_v2_accessor.c" \
    -o "$UNIT_OUTPUT/SourceV2AccessorTest"
"$UNIT_OUTPUT/SourceV2AccessorTest"

"$cc" -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/provider_header_smoke.c"
"$cxx" -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/provider_header_smoke.cpp"
printf 'PASS source V2 C11 and C++17 header/layout smoke\n'
