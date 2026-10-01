#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
package="$spike_root/SwiftPM"
source_template="$package/SourceTarget"
artifacts="$spike_root/.artifacts/swiftpm-source-target"
plugin_scratch="$artifacts/plugin-scratch"
source_package="$artifacts/source-package"
source_scratch="$artifacts/source-scratch"
selection="$package/Fixtures/contract-selection.txt"

rm -rf "$artifacts"
mkdir -p "$artifacts"
cp "$selection" "$artifacts/contract-selection.original"
restore_selection() {
    cp "$artifacts/contract-selection.original" "$selection"
}
trap restore_selection EXIT

dotnet_path="$(command -v dotnet)"
dotnet_root="$(cd "$(dirname "$dotnet_path")" && pwd -P)"
{
    dotnet --version
    swift --version
    xcodebuild -version
} > "$artifacts/tool-versions.log" 2>&1

sha256_file() {
    shasum -a 256 "$1" | awk '{ print $1 }'
}

plugin_build() {
    local label="$1"
    if ! DOTNET_ROOT="$dotnet_root" \
        swift build \
            --package-path "$package" \
            --scratch-path "$plugin_scratch" \
            --product ProjectionClient \
            -v > "$artifacts/plugin-$label.log" 2>&1; then
        tail -n 100 "$artifacts/plugin-$label.log" >&2
        exit 1
    fi
}

find_plugin_output() {
    local target="$1"
    local file="$2"
    find "$plugin_scratch/plugins/outputs" \
        -path "*/$target/*" -type f -name "$file" -print -quit
}

read_fixture_hash() {
    sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$1"
}

verify_projection_outputs() {
    local fixture="$1"
    local expected_hash
    expected_hash="$(read_fixture_hash "$fixture")"
    local swift_output header_output adapter_output swift_manifest header_manifest
    swift_output="$(find_plugin_output ProjectionClient NetLift.swift)"
    header_output="$(find_plugin_output ProjectionCABI netlift.h)"
    adapter_output="$(find_plugin_output ProjectionCABI abi.c)"
    swift_manifest="$(find_plugin_output ProjectionClient projection-manifest.json)"
    header_manifest="$(find_plugin_output ProjectionCABI projection-manifest.json)"
    if [[ -z "$swift_output" || -z "$header_output" || -z "$adapter_output" ||
          -z "$swift_manifest" || -z "$header_manifest" ]]; then
        echo "SwiftPM did not produce the declared per-contract Swift/C outputs." >&2
        exit 1
    fi

    local header_hash
    header_hash="$(sed -n 's/^#define LIFT_SCHEMA_HASH_HEX "\([[:xdigit:]]*\)"/\1/p' "$header_output")"
    if [[ "$header_hash" != "$expected_hash" ]] ||
        ! grep -Fq "\"schemaHash\":\"$expected_hash\"" "$swift_manifest" ||
        ! grep -Fq "\"schemaHash\":\"$expected_hash\"" "$header_manifest" ||
        ! grep -Fq 'projection_fixture_validate_config' "$adapter_output"; then
        echo "Generated Swift/header/C adapter outputs disagree with the selected schema." >&2
        exit 1
    fi

    printf '%s\n' "$swift_output" "$header_output" "$adapter_output" \
        "$swift_manifest" "$header_manifest"
}

save_contract_outputs() {
    local label="$1"
    local fixture="$2"
    local expected_hash
    expected_hash="$(read_fixture_hash "$fixture")"
    local contract_output="$artifacts/contracts/$expected_hash"
    local outputs
    outputs="$(verify_projection_outputs "$fixture")"
    local swift_output="${outputs%%$'\n'*}"
    outputs="${outputs#*$'\n'}"
    local header_output="${outputs%%$'\n'*}"
    outputs="${outputs#*$'\n'}"
    local adapter_output="${outputs%%$'\n'*}"
    outputs="${outputs#*$'\n'}"
    local swift_manifest="${outputs%%$'\n'*}"
    local header_manifest="${outputs#*$'\n'}"
    mkdir -p "$contract_output"
    cp "$swift_output" "$contract_output/NetLift.swift"
    cp "$header_output" "$contract_output/netlift.h"
    cp "$adapter_output" "$contract_output/abi.c"
    cp "$swift_manifest" "$contract_output/swift-manifest.json"
    cp "$header_manifest" "$contract_output/c-manifest.json"

    echo "$contract_output"
}

hash_contract_outputs() {
    local path="$1"
    {
        sha256_file "$path/NetLift.swift"
        sha256_file "$path/netlift.h"
        sha256_file "$path/abi.c"
        sha256_file "$path/swift-manifest.json"
        sha256_file "$path/c-manifest.json"
    } | shasum -a 256 | awk '{ print $1 }'
}

print_contract_hashes() {
    local path="$1"
    echo "SWIFT-SHA256: $(sha256_file "$path/NetLift.swift")"
    echo "HEADER-SHA256: $(sha256_file "$path/netlift.h")"
    echo "C-ADAPTER-SHA256: $(sha256_file "$path/abi.c")"
    echo "SWIFT-MANIFEST-SHA256: $(sha256_file "$path/swift-manifest.json")"
    echo "C-MANIFEST-SHA256: $(sha256_file "$path/c-manifest.json")"
}

mtime_contract_outputs() {
    local path="$1"
    stat -f '%m' \
        "$path/NetLift.swift" \
        "$path/netlift.h" \
        "$path/abi.c" \
        "$path/swift-manifest.json" \
        "$path/c-manifest.json"
}

stage_source_package() {
    local contract_output="$1"
    local consumer="$2"
    rm -rf "$source_package"
    mkdir -p \
        "$source_package/Sources/ProjectionCABI/include" \
        "$source_package/Sources/ProjectionClient"
    cp "$source_template/Package.swift" "$source_package/Package.swift"
    cp "$source_template/Sources/ProjectionCABI/include/NetLiftProjectionSupport.h" \
        "$source_package/Sources/ProjectionCABI/include/"
    cp "$contract_output/netlift.h" \
        "$source_package/Sources/ProjectionCABI/include/"
    cp "$contract_output/abi.c" "$source_package/Sources/ProjectionCABI/"
    cp "$contract_output/NetLift.swift" "$source_package/Sources/ProjectionClient/"
    cp "$source_template/Consumers/$consumer" "$source_package/Sources/ProjectionClient/main.swift"
}

build_source_package() {
    local label="$1"
    if ! swift build \
        --package-path "$source_package" \
        --scratch-path "$source_scratch" \
        -v > "$artifacts/source-$label-build.log" 2>&1; then
        tail -n 100 "$artifacts/source-$label-build.log" >&2
        exit 1
    fi
    if [[ "$label" == "implementation" ]]; then
        if grep -E 'Compile abi\.c|Compile NetLift\.swift' "$artifacts/source-$label-build.log"; then
            echo "Implementation-only edit recompiled staged generated sources." >&2
            exit 1
        fi
    elif ! grep -Fq '/Sources/ProjectionCABI/abi.c' "$artifacts/source-$label-build.log" ||
        ! grep -Fq 'Compile NetLift.swift' "$artifacts/source-$label-build.log"; then
            echo "SwiftPM did not compile the staged generated C and Swift sources." >&2
            exit 1
    fi
}

run_source_package() {
    local label="$1"
    local expected_hash="$2"
    local bin_path
    bin_path="$(swift build --show-bin-path \
        --package-path "$source_package" \
        --scratch-path "$source_scratch")"
    "$bin_path/ProjectionClient" > "$artifacts/source-$label-run.log"
    if ! grep -Fq "schema=$expected_hash" "$artifacts/source-$label-run.log"; then
        cat "$artifacts/source-$label-run.log" >&2
        echo "Swift consumer did not observe the selected generated schema hash." >&2
        exit 1
    fi
    cat "$artifacts/source-$label-run.log"
    shasum -a 256 "$bin_path/ProjectionClient" | awk '{ print "CONSUMER-BINARY-SHA256: " $1 }'
}

fixture="$package/Fixtures/ProjectionFixture.g.cs"
changed_fixture="$package/Fixtures/ProjectionFixture.ContractChanged.g.cs"
printf 'baseline\n' > "$selection"

echo "== Clean SwiftPM BuildToolPlugin projection =="
plugin_build clean
if ! grep -Fq 'warning: C source file generation not enabled:' "$artifacts/plugin-clean.log" ||
    ! grep -Fq 'warning: C header file generation not enabled:' "$artifacts/plugin-clean.log"; then
    echo "Expected SwiftPM generated C/header source-target limitations were not reported." >&2
    exit 1
fi
if [[ "$(grep -Fc 'PROJECTION-COMMAND-RUN:' "$artifacts/plugin-clean.log")" -ne 2 ]]; then
    echo "Clean plugin build did not run both projection commands." >&2
    exit 1
fi
grep -E 'PROJECTION-COMMAND-RUN:|warning: C (source|header) file generation not enabled:|Build complete!' \
    "$artifacts/plugin-clean.log" || true
baseline_contract="$(save_contract_outputs baseline "$fixture")"
baseline_hash="$(read_fixture_hash "$fixture")"
baseline_output_hash="$(hash_contract_outputs "$baseline_contract")"
baseline_output_mtime="$(mtime_contract_outputs "$baseline_contract")"
echo "CONTRACT-OUTPUT: $baseline_contract"
echo "CONTRACT-SCHEMA-SHA256: $baseline_hash"
print_contract_hashes "$baseline_contract"
echo "CONTRACT-OUTPUT-SET-SHA256: $baseline_output_hash"

stage_source_package "$baseline_contract" main-baseline.swift
echo "== Clean SwiftPM source+C target compile and run =="
build_source_package clean
run_source_package baseline "$baseline_hash"
baseline_binary="$(swift build --show-bin-path \
    --package-path "$source_package" \
    --scratch-path "$source_scratch")/ProjectionClient"
baseline_binary_hash="$(sha256_file "$baseline_binary")"

echo "== Implementation-only managed edit =="
managed_root="$artifacts/managed-fixture"
managed_bin="$artifacts/managed-bin"
mkdir -p "$managed_root"
cp "$spike_root/Managed/"* "$managed_root/"
managed_dll="$managed_bin/bin/BuildLoop.Managed/debug/BuildLoop.Managed.dll"
dotnet build "$managed_root/BuildLoop.Managed.csproj" -c Debug \
    --artifacts-path "$managed_bin" --nologo > "$artifacts/managed-baseline.log"
managed_hash_before="$(sha256_file "$managed_dll")"
sed 's/draft.Quantity}"/draft.Quantity}:implementation"/' "$managed_root/Implementation.cs" \
    > "$managed_root/Implementation.cs.tmp"
mv "$managed_root/Implementation.cs.tmp" "$managed_root/Implementation.cs"
dotnet build "$managed_root/BuildLoop.Managed.csproj" -c Debug \
    --artifacts-path "$managed_bin" --nologo > "$artifacts/managed-implementation.log"
managed_hash_after="$(sha256_file "$managed_dll")"
if [[ "$managed_hash_after" == "$managed_hash_before" ]]; then
    echo "Implementation-only edit did not change the managed assembly." >&2
    exit 1
fi
echo "MANAGED-DLL-SHA256-BEFORE: $managed_hash_before"
echo "MANAGED-DLL-SHA256-AFTER:  $managed_hash_after"

plugin_build implementation
if grep -Fq 'PROJECTION-COMMAND-RUN:' "$artifacts/plugin-implementation.log" ||
    [[ "$(hash_contract_outputs "$baseline_contract")" != "$baseline_output_hash" ||
       "$(mtime_contract_outputs "$baseline_contract")" != "$baseline_output_mtime" ]]; then
    echo "Implementation-only edit reran projection or changed contract outputs." >&2
    exit 1
fi
build_source_package implementation
implementation_binary_hash="$(sha256_file "$baseline_binary")"
if [[ "$implementation_binary_hash" != "$baseline_binary_hash" ]]; then
    echo "Implementation-only edit changed the staged SwiftPM consumer binary." >&2
    exit 1
fi
run_source_package implementation "$baseline_hash"
echo "PASS implementation-only managed edit changed only its DLL; projection and SwiftPM source target stayed up-to-date"

echo "== Contract-only schema edit =="
printf 'contract-changed\n' > "$selection"
plugin_build contract
if [[ "$(grep -Fc 'PROJECTION-COMMAND-RUN:' "$artifacts/plugin-contract.log")" -ne 2 ]]; then
    echo "Contract selection change did not rerun both projection commands." >&2
    exit 1
fi
grep -E 'PROJECTION-COMMAND-RUN:|PROJECTION-MANIFEST:|Build complete!' \
    "$artifacts/plugin-contract.log" || true
changed_contract="$(save_contract_outputs contract "$changed_fixture")"
changed_hash="$(read_fixture_hash "$changed_fixture")"
changed_output_hash="$(hash_contract_outputs "$changed_contract")"
if [[ "$changed_hash" == "$baseline_hash" || "$changed_output_hash" == "$baseline_output_hash" ]] ||
    ! grep -Fq 'public struct Vector' "$changed_contract/NetLift.swift"; then
    echo "Contract edit did not produce a distinct typed Swift/C projection." >&2
    exit 1
fi
echo "CONTRACT-OUTPUT: $changed_contract"
echo "CONTRACT-SCHEMA-SHA256: $changed_hash"
print_contract_hashes "$changed_contract"
echo "CONTRACT-OUTPUT-SET-SHA256: $changed_output_hash"

stage_source_package "$changed_contract" main-contract-changed.swift
echo "== Contract-changed SwiftPM source+C target compile and run =="
build_source_package contract
run_source_package contract "$changed_hash"
if ! grep -Fq 'vector=12' "$artifacts/source-contract-run.log"; then
    echo "Contract-changed Swift consumer did not exercise the newly projected Vector DTO." >&2
    exit 1
fi
echo "PASS generated Swift DTO, generated C header, and generated C adapter compiled and ran from the artifact-only SwiftPM package"
echo "PASS SwiftPM plugin outputs are staged outside authored sources; no generated files entered source targets in the checkout"
echo "Logs and per-contract generated sources retained under $artifacts"
