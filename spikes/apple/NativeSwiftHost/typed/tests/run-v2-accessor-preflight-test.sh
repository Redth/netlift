#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
artifact_dir="$script_dir/.artifacts/v2-accessor-preflight"
mkdir -p "$artifact_dir"

cc=${CC:-clang}
for enabled in 0 1; do
    output="$artifact_dir/CallbackLifecycleV2AccessorTest-$enabled"
    "$cc" \
        -std=c11 \
        -Wall -Wextra -Werror \
        -DNETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2="$enabled" \
        -I "$script_dir" \
        -I "$script_dir/GeneratedTyped" \
        "$script_dir/CallbackLifecycleV2State.c" \
        "$script_dir/CallbackLifecycleV2Accessor.c" \
        "$script_dir/tests/CallbackLifecycleV2AccessorTest.c" \
        -o "$output"
    "$output"
done
