#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
generated="$script_dir/GeneratedTyped"
artifact_root="$script_dir/.artifacts"
mkdir -p "$artifact_root"

if [ ! -f "$generated/module.modulemap" ] ||
    [ ! -f "$generated/NetLift.swift" ] ||
    [ ! -f "$generated/netlift.h" ]; then
    echo "Typed facade files are missing; generating the isolated schema-v3 facade." >&2
    sh "$script_dir/generate-typed-facade.sh"
fi

for generated_file in \
    "$generated/module.modulemap" \
    "$generated/NetLift.swift" \
    "$generated/netlift.h"; do
    if [ ! -f "$generated_file" ]; then
        echo "Typed facade generation did not create required file: $generated_file" >&2
        exit 66
    fi
done

dotnet run \
    --project "$script_dir/tests/TypedReplyDescriptorTest.csproj" \
    -c Release \
    --artifacts-path "$artifact_root/dotnet-test-artifacts"

output_dir=$(mktemp -d "$artifact_root/host-stub-test.XXXXXX")
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
    -c "$script_dir/tests/TypedReplyRuntimeStubs.c" \
    -o "$output_dir/TypedReplyRuntimeStubs.o"
"$swiftc" \
    -sdk "$macos_sdk" \
    -I "$generated" \
    "$generated/NetLift.swift" \
    "$script_dir/tests/TypedReplyHostStubTest.swift" \
    "$output_dir/GeneratedFacadeAdapter.o" \
    "$output_dir/TypedReplyRuntimeStubs.o" \
    -o "$output_dir/TypedReplyHostStubTest"
"$output_dir/TypedReplyHostStubTest"
printf 'Typed host-stub test artifact: %s\n' "$output_dir/TypedReplyHostStubTest"
