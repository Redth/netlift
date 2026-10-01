#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export DEVELOPER_DIR=${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}
artifact_root="$script_dir/.artifacts"
generated="$script_dir/Generated"

if [ ! -f "$generated/module.modulemap" ] ||
    [ ! -f "$generated/NetLift.swift" ] ||
    [ ! -f "$generated/netlift.h" ]; then
    echo "Generated facade files are missing; generating them for the lifecycle test." >&2
    sh "$script_dir/generate-facade.sh"
fi

for generated_file in \
    "$generated/module.modulemap" \
    "$generated/NetLift.swift" \
    "$generated/netlift.h"; do
    if [ ! -f "$generated_file" ]; then
        echo "Facade generation did not create required file: $generated_file" >&2
        exit 66
    fi
done

mkdir -p "$artifact_root"
output_dir=$(mktemp -d "$artifact_root/lifecycle-test.XXXXXX")
xcrun --find clang >/dev/null
xcrun --find swiftc >/dev/null

clang=$(xcrun --find clang)
swiftc=$(xcrun --find swiftc)
macos_sdk=$(xcrun --sdk macosx --show-sdk-path)

"$clang" \
    -std=c11 \
    -Wall -Wextra -Werror \
    -pthread \
    -isysroot "$macos_sdk" \
    -I "$script_dir" \
    -I "$generated" \
    -c "$script_dir/GeneratedFacadeAdapter.c" \
    -o "$output_dir/GeneratedFacadeAdapter.o"
"$clang" \
    -std=c11 \
    -Wall -Wextra -Werror \
    -pthread \
    -isysroot "$macos_sdk" \
    -I "$script_dir" \
    -I "$generated" \
    -c "$script_dir/tests/LifecycleRuntimeStubs.c" \
    -o "$output_dir/LifecycleRuntimeStubs.o"
"$swiftc" \
    -sdk "$macos_sdk" \
    -I "$generated" \
    "$generated/NetLift.swift" \
    "$script_dir/tests/GeneratedFacadeLifecycleTest.swift" \
    "$output_dir/GeneratedFacadeAdapter.o" \
    "$output_dir/LifecycleRuntimeStubs.o" \
    -o "$output_dir/GeneratedFacadeLifecycleTest"
"$output_dir/GeneratedFacadeLifecycleTest"
printf 'Lifecycle test artifact: %s\n' "$output_dir/GeneratedFacadeLifecycleTest"
