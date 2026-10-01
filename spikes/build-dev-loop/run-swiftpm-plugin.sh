#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
artifacts="$spike_root/.artifacts/swiftpm-plugin"
package="$spike_root/SwiftPM"
fixture="$package/Fixtures/ProjectionFixture.g.cs"
business_project="$spike_root/Managed/BuildLoop.Managed.csproj"
business_root="$artifacts/managed-fixture"
business_implementation="$business_root/Implementation.cs"
business_dll="$artifacts/managed-bin/bin/BuildLoop.Managed/debug/BuildLoop.Managed.dll"

rm -rf "$artifacts"
mkdir -p "$artifacts"
mkdir -p "$business_root"
cp "$spike_root/Managed/"* "$business_root/"

dotnet build "$business_root/BuildLoop.Managed.csproj" -c Debug \
    --artifacts-path "$artifacts/managed-bin" --nologo \
    > "$artifacts/managed-baseline.log"
if [[ ! -f "$business_dll" ]]; then
    echo "Expected the built managed fixture at $business_dll" >&2
    exit 1
fi
business_hash_before="$(shasum -a 256 "$business_dll" | awk '{ print $1 }')"

dotnet_path="$(command -v dotnet)"
dotnet_root="$(cd "$(dirname "$dotnet_path")" && pwd -P)"
scratch="$artifacts/swiftpm"

if ! DOTNET_ROOT="$dotnet_root" \
    swift build \
        --package-path "$package" \
        --scratch-path "$scratch" \
        --product ProjectionClient \
        -v > "$artifacts/first-build.log" 2>&1; then
    tail -n 80 "$artifacts/first-build.log" >&2
    exit 1
fi
grep -E 'PROJECTION-COMMAND-RUN:|PROJECTION-MANIFEST:|Build complete!' \
    "$artifacts/first-build.log" || true

swift_output="$(find "$scratch/plugins/outputs" -path '*/ProjectionClient/*' -type f -name NetLift.swift -print -quit)"
header_output="$(find "$scratch/plugins/outputs" -path '*/ProjectionCABI/*' -type f -name netlift.h -print -quit)"
abi_source="$(find "$scratch/plugins/outputs" -path '*/ProjectionCABI/*' -type f -name abi.c -print -quit)"
swift_manifest="$(find "$scratch/plugins/outputs" -path '*/ProjectionClient/*' -type f -name projection-manifest.json -print -quit)"
header_manifest="$(find "$scratch/plugins/outputs" -path '*/ProjectionCABI/*' -type f -name projection-manifest.json -print -quit)"
if [[ -z "$swift_output" || -z "$header_output" || -z "$abi_source" ||
      -z "$swift_manifest" || -z "$header_manifest" ]]; then
    echo "SwiftPM did not leave the declared Swift/C/header outputs and manifests." >&2
    exit 1
fi
output_directory="$(dirname "$swift_output")"
expected_hash="$(sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$fixture")"
header_hash="$(sed -n 's/^#define LIFT_SCHEMA_HASH_HEX "\([[:xdigit:]]*\)"/\1/p' "$header_output")"
if [[ -z "$expected_hash" || "$header_hash" != "$expected_hash" ]] ||
    ! grep -Fq "\"schemaHash\":\"$expected_hash\"" "$swift_manifest" ||
    ! grep -Fq "\"schemaHash\":\"$expected_hash\"" "$header_manifest"; then
    echo "Generated header/manifest hashes do not match the declared fixture schema." >&2
    exit 1
fi
echo "PASS SwiftPM derived Swift/header schema hash: $expected_hash"
echo "SWIFTPM-DERIVED-SWIFT: $output_directory"
echo "SWIFTPM-DERIVED-HEADER: $(dirname "$header_output")"

hash_outputs() {
    shasum -a 256 \
        "$swift_output" \
        "$header_output" \
        "$abi_source" \
        "$swift_manifest" \
        "$header_manifest" |
        shasum -a 256 | awk '{ print $1 }'
}

output_hash_before="$(hash_outputs)"
output_mtime_before="$(stat -f '%m' \
    "$swift_output" \
    "$header_output" \
    "$abi_source" \
    "$swift_manifest" \
    "$header_manifest")"

sleep 2
sed 's/draft.Quantity}"/draft.Quantity}:managed"/' "$business_implementation" \
    > "$business_implementation.tmp"
mv "$business_implementation.tmp" "$business_implementation"
dotnet build "$business_root/BuildLoop.Managed.csproj" -c Debug \
    --artifacts-path "$artifacts/managed-bin" --nologo \
    > "$artifacts/managed-implementation.log"
business_hash_after="$(shasum -a 256 "$business_dll" | awk '{ print $1 }')"
if [[ "$business_hash_after" == "$business_hash_before" ]]; then
    echo "Implementation-only edit did not change the managed library." >&2
    exit 1
fi
echo "PASS implementation-only edit rebuilt the managed library"

if ! DOTNET_ROOT="$dotnet_root" \
    swift build \
        --package-path "$package" \
        --scratch-path "$scratch" \
        --product ProjectionClient \
        -v > "$artifacts/implementation-build.log" 2>&1; then
    tail -n 80 "$artifacts/implementation-build.log" >&2
    exit 1
fi
grep -E 'PROJECTION-COMMAND-RUN:|Build complete!' \
    "$artifacts/implementation-build.log" || true

output_hash_after="$(hash_outputs)"
output_mtime_after="$(stat -f '%m' \
    "$swift_output" \
    "$header_output" \
    "$abi_source" \
    "$swift_manifest" \
    "$header_manifest")"
if grep -Fq 'PROJECTION-COMMAND-RUN:' "$artifacts/implementation-build.log"; then
    echo "SwiftPM reran projection generation for an implementation-only edit." >&2
    exit 1
fi
if [[ "$output_hash_after" != "$output_hash_before" ||
      "$output_mtime_after" != "$output_mtime_before" ]]; then
    echo "Implementation-only edit changed a derived output's bytes or modification time." >&2
    exit 1
fi
echo "PASS implementation-only edit did not rerun projection or rewrite derived outputs"
