#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
template="$spike_root/XcodeNative"
artifacts="$spike_root/.artifacts/xcode-native-target"
build_project="$artifacts/Project"
derived_data="$artifacts/DerivedData"
selection="$build_project/contract-selection.txt"
managed_source="$artifacts/managed-fixture/Implementation.cs"
managed_project="$artifacts/managed-fixture/BuildLoop.Managed.csproj"
managed_artifacts="$artifacts/managed-bin"
managed_dll="$managed_artifacts/bin/BuildLoop.Managed/debug/BuildLoop.Managed.dll"
app_executable="$derived_data/Build/Products/Debug/ProjectionHost.app/Contents/MacOS/ProjectionHost"

rm -rf "$artifacts"
mkdir -p "$artifacts"
cp -R "$template/." "$build_project"
{
    dotnet --version
    swift --version
    xcodebuild -version
    xcodebuild -showsdks
} > "$artifacts/tool-versions.log" 2>&1

fixture_hash() {
    sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$1"
}

hash_file() {
    shasum -a 256 "$1" | awk '{ print $1 }'
}

run_xcodebuild() {
    local label="$1"
    if ! xcodebuild \
        -project "$build_project/ProjectionHost.xcodeproj" \
        -scheme ProjectionHost \
        -configuration Debug \
        -derivedDataPath "$derived_data" \
        NETLIFT_REPOSITORY_ROOT="$repo_root" \
        CODE_SIGNING_ALLOWED=NO \
        > "$artifacts/xcode-$label.log" 2>&1; then
        if [[ "$label" != "corrupted" ]]; then
            tail -n 100 "$artifacts/xcode-$label.log" >&2
        fi
        return 1
    fi
}

run_host() {
    local label="$1"
    local expected_hash="$2"
    local expected_values="$3"
    if [[ ! -x "$app_executable" ]]; then
        echo "Xcode native executable is missing: $app_executable" >&2
        exit 1
    fi
    "$app_executable" > "$artifacts/host-$label.log"
    if ! grep -Fq "$expected_values" "$artifacts/host-$label.log" ||
        ! grep -Fq "schema=$expected_hash" "$artifacts/host-$label.log"; then
        cat "$artifacts/host-$label.log" >&2
        echo "Native Xcode target did not produce the expected contract result." >&2
        exit 1
    fi
    cat "$artifacts/host-$label.log"
    echo "NATIVE-EXECUTABLE-SHA256: $(hash_file "$app_executable")"
}

assert_no_generated_member_compiles() {
    local log="$1"
    if grep -E 'SwiftCompile normal .* (NetLift\.swift|NativeMain\.swift)|CompileC .*abi\.c' "$log"; then
        echo "Unchanged generated Xcode members were recompiled." >&2
        return 1
    fi
}

generated_dir=""
manifest_hash() {
    sed -n 's/^schemaHash=//p' "$generated_dir/projection-manifest.txt"
}
output_hashes() {
    shasum -a 256 \
        "$generated_dir/NetLift.swift" \
        "$generated_dir/netlift.h" \
        "$generated_dir/abi.c" \
        "$generated_dir/NativeMain.swift" \
        "$generated_dir/projection-manifest.txt" |
        shasum -a 256 | awk '{ print $1 }'
}
output_mtimes() {
    stat -f '%m' \
        "$generated_dir/NetLift.swift" \
        "$generated_dir/netlift.h" \
        "$generated_dir/abi.c" \
        "$generated_dir/NativeMain.swift" \
        "$generated_dir/projection-manifest.txt"
}
assert_target_compiled_generated_members() {
    local log="$1"
    if ! grep -Fq "$generated_dir/NetLift.swift" "$log" ||
        ! grep -Fq "$generated_dir/abi.c" "$log" ||
        ! grep -Fq "$generated_dir/NativeMain.swift" "$log"; then
        echo "Xcode did not compile all generated Swift and C target members." >&2
        return 1
    fi
}

printf 'baseline\n' > "$selection"
echo "== Clean Xcode native target build =="
run_xcodebuild clean
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$artifacts/xcode-clean.log" ||
    ! grep -Fq 'XCODE-PROJECTION-VALIDATE:' "$artifacts/xcode-clean.log"; then
    echo "Clean Xcode build did not execute declared generation and validation phases." >&2
    exit 1
fi
generated_dir="$(find "$derived_data/Build/Intermediates.noindex" \
    -type d -path '*/DerivedSources/NetLiftProjection' -print -quit)"
if [[ -z "$generated_dir" ]]; then
    echo "Xcode did not create its declared DerivedSources directory." >&2
    exit 1
fi
assert_target_compiled_generated_members "$artifacts/xcode-clean.log"
expected_hash="$(fixture_hash "$build_project/fixtures/ProjectionFixture.g.cs")"
if [[ "$expected_hash" != "$(manifest_hash)" ]]; then
    echo "Clean Xcode target's generated output schema hash mismatch." >&2
    exit 1
fi
grep -E 'XCODE-PROJECTION-RUN:|XCODE-PROJECTION-MANIFEST:|XCODE-PROJECTION-VALIDATE:|SwiftCompile normal .* (NetLift\.swift|NativeMain\.swift)|CompileC .*abi\.c|BUILD SUCCEEDED' \
    "$artifacts/xcode-clean.log" || true
baseline_output_hash="$(output_hashes)"
baseline_output_mtimes="$(output_mtimes)"
echo "BASELINE-SCHEMA-SHA256: $expected_hash"
echo "BASELINE-PROJECTION-OUTPUT-SET-SHA256: $baseline_output_hash"
run_host baseline "$expected_hash" 'point=7 method=7 abi=1'
baseline_app_hash="$(hash_file "$app_executable")"

echo "== Corrupt generated source must fail validation =="
printf '\n// deliberate integrity-test corruption\n' >> "$generated_dir/NetLift.swift"
if run_xcodebuild corrupted; then
    echo "Xcode accepted a corrupted generated source without regeneration." >&2
    exit 1
fi
if ! grep -Fq 'Generated source integrity mismatch for NetLift.swift' "$artifacts/xcode-corrupted.log"; then
    echo "Corrupted generated source did not fail at the projection integrity check." >&2
    exit 1
fi
echo "PASS corrupt generated Swift failed the always-on integrity phase"

echo "== Missing generated source must repair =="
rm "$generated_dir/NetLift.swift"
run_xcodebuild repaired
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$artifacts/xcode-repaired.log" ||
    ! grep -Fq "$generated_dir/NetLift.swift" "$artifacts/xcode-repaired.log"; then
    echo "Deleting generated Swift did not rerun generation and compile the repaired output." >&2
    exit 1
fi
if [[ "$(output_hashes)" != "$baseline_output_hash" ]]; then
    echo "Repair build did not restore the original generated source bytes." >&2
    exit 1
fi
baseline_output_mtimes="$(output_mtimes)"
echo "PASS missing generated Swift was regenerated and compiled"

echo "== Implementation-only managed edit =="
mkdir -p "$artifacts/managed-fixture"
cp "$spike_root/Managed/"* "$artifacts/managed-fixture/"
dotnet build "$managed_project" -c Debug \
    --artifacts-path "$managed_artifacts" --nologo \
    > "$artifacts/managed-baseline.log"
managed_hash_before="$(hash_file "$managed_dll")"
sed 's/draft.Quantity}"/draft.Quantity}:implementation"/' "$managed_source" \
    > "$managed_source.tmp"
mv "$managed_source.tmp" "$managed_source"
dotnet build "$managed_project" -c Debug \
    --artifacts-path "$managed_artifacts" --nologo \
    > "$artifacts/managed-implementation.log"
managed_hash_after="$(hash_file "$managed_dll")"
if [[ "$managed_hash_before" == "$managed_hash_after" ]]; then
    echo "Implementation-only edit did not change the managed library." >&2
    exit 1
fi
run_xcodebuild implementation
if grep -Fq 'XCODE-PROJECTION-RUN:' "$artifacts/xcode-implementation.log" ||
    [[ "$(output_hashes)" != "$baseline_output_hash" ||
       "$(output_mtimes)" != "$baseline_output_mtimes" ]]; then
    echo "Implementation-only edit reran generation or rewrote its outputs." >&2
    exit 1
fi
assert_no_generated_member_compiles "$artifacts/xcode-implementation.log"
if [[ "$(hash_file "$app_executable")" != "$baseline_app_hash" ]]; then
    echo "Implementation-only edit changed the native app executable." >&2
    exit 1
fi
echo "MANAGED-DLL-SHA256-BEFORE: $managed_hash_before"
echo "MANAGED-DLL-SHA256-AFTER:  $managed_hash_after"
echo "PASS implementation-only edit changed managed DLL only; Xcode generation skipped"
run_host implementation "$expected_hash" 'point=7 method=7 abi=1'

echo "== Contract-only edit =="
printf 'contract-changed\n' > "$selection"
run_xcodebuild contract
if ! grep -Fq 'XCODE-PROJECTION-RUN:' "$artifacts/xcode-contract.log" ||
    ! grep -Fq "$generated_dir/NetLift.swift" "$artifacts/xcode-contract.log" ||
    ! grep -Fq "$generated_dir/abi.c" "$artifacts/xcode-contract.log"; then
    echo "Contract-only edit did not rerun projection and compile generated sources." >&2
    exit 1
fi
contract_hash="$(fixture_hash "$build_project/fixtures/ProjectionFixture.ContractChanged.g.cs")"
if [[ "$contract_hash" == "$expected_hash" || "$contract_hash" != "$(manifest_hash)" ]]; then
    echo "Contract-only edit did not produce the selected per-contract projection." >&2
    exit 1
fi
grep -E 'XCODE-PROJECTION-RUN:|XCODE-PROJECTION-MANIFEST:|XCODE-PROJECTION-VALIDATE:|SwiftCompile normal .* (NetLift\.swift|NativeMain\.swift)|CompileC .*abi\.c|BUILD SUCCEEDED' \
    "$artifacts/xcode-contract.log" || true
echo "BASELINE-SCHEMA-SHA256: $expected_hash"
echo "CONTRACT-SCHEMA-SHA256: $contract_hash"
contract_output_hash="$(output_hashes)"
echo "CONTRACT-OUTPUT-SET-SHA256: $contract_output_hash"
run_host contract "$contract_hash" 'point=7 vector=12 method=19 abi=1'
contract_app_hash="$(hash_file "$app_executable")"

echo "== Native-only source edit =="
native_source="$build_project/NativeOnly.swift"
sed 's/native-v1/native-v2/' "$native_source" > "$native_source.tmp"
mv "$native_source.tmp" "$native_source"
contract_output_hash="$(output_hashes)"
contract_output_mtimes="$(output_mtimes)"
run_xcodebuild native
if grep -Fq 'XCODE-PROJECTION-RUN:' "$artifacts/xcode-native.log" ||
    [[ "$(output_hashes)" != "$contract_output_hash" ||
       "$(output_mtimes)" != "$contract_output_mtimes" ]]; then
    echo "Native-only source edit unexpectedly reran generation or rewrote outputs." >&2
    exit 1
fi
assert_no_generated_member_compiles "$artifacts/xcode-native.log"
if [[ "$(hash_file "$app_executable")" == "$contract_app_hash" ]]; then
    echo "Native-only source edit did not update the app executable." >&2
    exit 1
fi
echo "PASS native-only Swift edit rebuilt the target while projection stayed up-to-date"
run_host native "$contract_hash" 'native=native-v2'

echo "PASS Xcode target compiled and linked task-produced generated Swift, C, and header outputs from DerivedSources"
echo "Xcode logs and DerivedSources retained under $artifacts"
