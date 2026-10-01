#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
artifact_dir="$script_dir/.artifacts/callback-lifecycle-v2"
mkdir -p "$artifact_dir"

cc=${CC:-clang}
"$cc" \
    -std=c11 \
    -Wall -Wextra -Werror \
    -pthread \
    -DNETLIFT_V2_TESTING \
    -I "$script_dir" \
    -I "$script_dir/GeneratedTyped" \
    "$script_dir/CallbackLifecycleV2State.c" \
    "$script_dir/tests/CallbackLifecycleV2Test.c" \
    -o "$artifact_dir/CallbackLifecycleV2Test"
"$artifact_dir/CallbackLifecycleV2Test"
