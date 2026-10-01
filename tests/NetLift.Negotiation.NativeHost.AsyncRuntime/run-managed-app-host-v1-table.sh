#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST_DIR="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
CORE_DIR="$ROOT/src/NetLift.NativeHost.V5"

if [[ -z "${NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_ARTIFACTS to an isolated artifact directory.\n' >&2
    exit 2
fi

ARTIFACT_ROOT="$(mkdir -p "$NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_ARTIFACTS" && cd "$NETLIFT_NATIVEHOST_MANAGED_APP_HOST_V1_ARTIFACTS" && pwd)"
OUTPUT_DIR="$ARTIFACT_ROOT/managed-app-host-v1-table"
mkdir -p "$OUTPUT_DIR"

cc=${CC:-clang}
cxx=${CXX:-clang++}

"$cc" -std=c11 -Wall -Wextra -Werror \
    -I "$CORE_DIR" \
    "$CORE_DIR/managed_app_host_v1.c" \
    "$HOST_DIR/managed_app_host_v1_table_test.c" \
    -o "$OUTPUT_DIR/managed-app-host-v1-table-test"
"$OUTPUT_DIR/managed-app-host-v1-table-test"

"$cc" -std=c11 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/managed_app_host_v1_header_smoke.c"
"$cxx" -std=c++17 -Wall -Wextra -Werror -I "$CORE_DIR" \
    -fsyntax-only "$HOST_DIR/managed_app_host_v1_header_smoke.cpp"
printf 'PASS managed app host V1 C11/C++17 header/layout smoke\n'
