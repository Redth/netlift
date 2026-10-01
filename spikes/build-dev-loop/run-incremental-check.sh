#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
mkdir -p "$spike_root/.artifacts"
work_root="$(mktemp -d "$spike_root/.artifacts/incremental.XXXXXX")"
trap 'rm -rf "$work_root"' EXIT

mkdir -p "$work_root/managed" "$work_root/native" "$work_root/generated"
cp "$spike_root/Managed/"* "$work_root/managed/"
cp "$spike_root/Native/PreviewFixture.swift" "$work_root/native/"

project="$work_root/managed/BuildLoop.Managed.csproj"
contract="$work_root/managed/Contract.cs"
implementation="$work_root/managed/Implementation.cs"
generated="$work_root/generated"
managed_dll="$work_root/artifacts/bin/BuildLoop.Managed/debug/BuildLoop.Managed.dll"

build_managed() {
    local phase="$1"
    local log="$work_root/build-$phase.log"
    dotnet build "$project" -c Debug --artifacts-path "$work_root/artifacts" --nologo -v:diag > "$log"
    if grep -Fq 'Task "Csc"' "$log"; then
        echo "MANAGED-COMPILE: $phase"
    else
        echo "MANAGED-COMPILE-SKIP: $phase"
    fi
}

hash_file() {
    shasum -a 256 "$1" | awk '{ print $1 }'
}

hash_generated() {
    shasum -a 256 "$generated/schema.json" "$generated/NetLift.swift" "$generated/NetLift.kt" |
        shasum -a 256 | awk '{ print $1 }'
}

replace_once() {
    local file="$1"
    local before="$2"
    local after="$3"
    if ! grep -Fq "$before" "$file"; then
        echo "Could not find expected text in $file: $before" >&2
        exit 1
    fi
    sed "s|$before|$after|" "$file" > "$file.tmp"
    mv "$file.tmp" "$file"
}

echo "== Baseline managed build and contract generation =="
build_managed baseline
if ! grep -Fq 'Task "Csc"' "$work_root/build-baseline.log"; then
    echo "Baseline build did not invoke the C# compiler." >&2
    exit 1
fi
bash "$spike_root/generate-contract.sh" "$contract" "$generated"
base_assembly="$(hash_file "$managed_dll")"
base_generated="$(hash_generated)"
bash "$spike_root/generate-contract.sh" "$contract" "$generated"

echo "== Generator output repair regression =="
mkdir -p "$work_root/expected"
cp "$generated/.contract-sha256" "$generated/schema.json" \
    "$generated/NetLift.swift" "$generated/NetLift.kt" "$work_root/expected/"
rm "$generated/NetLift.swift"
printf 'corrupt output\n' > "$generated/NetLift.kt"
repair_log="$(bash "$spike_root/generate-contract.sh" "$contract" "$generated")"
printf '%s\n' "$repair_log"
if [[ "$repair_log" != *"GENERATOR-RUN:"* ]]; then
    echo "Generator skipped despite missing/corrupt outputs and a matching stamp." >&2
    exit 1
fi
for output in .contract-sha256 schema.json NetLift.swift NetLift.kt; do
    if ! cmp -s "$work_root/expected/$output" "$generated/$output"; then
        echo "Generator failed to restore expected output bytes: $output" >&2
        exit 1
    fi
done
if [[ "$(hash_generated)" != "$base_generated" ]]; then
    echo "Output repair changed generated content for an unchanged contract." >&2
    exit 1
fi
echo "PASS generator repaired deleted/corrupt outputs with matching stamp"

echo "== Implementation-only edit =="
replace_once "$implementation" 'draft.Quantity}";' 'draft.Quantity}:managed";'
build_managed implementation
if ! grep -Fq 'Task "Csc"' "$work_root/build-implementation.log"; then
    echo "Implementation edit did not invoke the C# compiler." >&2
    exit 1
fi
bash "$spike_root/generate-contract.sh" "$contract" "$generated"
implementation_assembly="$(hash_file "$managed_dll")"
implementation_generated="$(hash_generated)"
if [[ "$implementation_assembly" == "$base_assembly" ]]; then
    echo "Implementation edit did not change the managed assembly." >&2
    exit 1
fi
if [[ "$implementation_generated" != "$base_generated" ]]; then
    echo "Implementation edit unexpectedly changed contract-generated outputs." >&2
    exit 1
fi
echo "PASS implementation edit rebuilt managed assembly only"

echo "== Public contract edit =="
replace_once "$contract" 'int Quantity' 'int Quantity, string Region'
build_managed contract
if ! grep -Fq 'Task "Csc"' "$work_root/build-contract.log"; then
    echo "Contract edit did not invoke the C# compiler." >&2
    exit 1
fi
bash "$spike_root/generate-contract.sh" "$contract" "$generated"
contract_assembly="$(hash_file "$managed_dll")"
contract_generated="$(hash_generated)"
if [[ "$contract_assembly" == "$implementation_assembly" ]]; then
    echo "Contract edit did not change the managed assembly." >&2
    exit 1
fi
if [[ "$contract_generated" == "$implementation_generated" ]]; then
    echo "Contract edit did not change generated contract outputs." >&2
    exit 1
fi
echo "PASS public contract edit rebuilt managed assembly and regenerated schema/Swift/Kotlin outputs"

echo "== Native-only edit =="
replace_once "$work_root/native/PreviewFixture.swift" 'previewLabel = "initial"' 'previewLabel = "native-only"'
build_managed native
if grep -Fq 'Task "Csc"' "$work_root/build-native.log"; then
    echo "Native-only edit unexpectedly invoked the C# compiler." >&2
    exit 1
fi
bash "$spike_root/generate-contract.sh" "$contract" "$generated"
swiftc -typecheck "$generated/NetLift.swift" "$work_root/native/PreviewFixture.swift"
native_assembly="$(hash_file "$managed_dll")"
native_generated="$(hash_generated)"
if [[ "$native_assembly" != "$contract_assembly" ]]; then
    echo "Native-only edit unexpectedly changed the managed assembly." >&2
    exit 1
fi
if [[ "$native_generated" != "$contract_generated" ]]; then
    echo "Native-only edit unexpectedly changed contract-generated outputs." >&2
    exit 1
fi
echo "PASS native-only edit typechecked against generated API without managed or generation changes"

echo "PASS incremental dependency check"
echo "  implementation: managed assembly changed; generated API unchanged"
echo "  contract:       managed assembly and generated API changed"
echo "  native-only:    native typecheck ran; managed assembly and generated API unchanged"
