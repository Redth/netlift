#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
    echo "Usage: generate-contract.sh <contract-source> <output-directory>" >&2
    exit 2
fi

contract_source="$1"
output_directory="$2"
contract_hash="$(shasum -a 256 "$contract_source" | awk '{ print $1 }')"
mkdir -p "$output_directory"
stage_directory="$(mktemp -d "$output_directory/.generate.XXXXXX")"
trap 'rm -rf "$stage_directory"' EXIT

stamp="$output_directory/.contract-sha256"

printf '%s\n' "$contract_hash" > "$stage_directory/.contract-sha256"
printf '{\n  "schemaVersion": 1,\n  "bridgeAbiVersion": 1,\n  "contractSha256": "%s"\n}\n' \
    "$contract_hash" > "$stage_directory/schema.json"
printf '// contract-sha256: %s\nenum NetLiftGeneratedContract {\n    static let schemaHash = "%s"\n}\n' \
    "$contract_hash" "$contract_hash" > "$stage_directory/NetLift.swift"
printf '// contract-sha256: %s\ninternal object NetLiftGeneratedContract {\n    const val schemaHash = "%s"\n}\n' \
    "$contract_hash" "$contract_hash" > "$stage_directory/NetLift.kt"

if [[ -f "$stamp" ]] &&
    cmp -s "$stage_directory/.contract-sha256" "$stamp" &&
    cmp -s "$stage_directory/schema.json" "$output_directory/schema.json" &&
    cmp -s "$stage_directory/NetLift.swift" "$output_directory/NetLift.swift" &&
    cmp -s "$stage_directory/NetLift.kt" "$output_directory/NetLift.kt"; then
    echo "GENERATOR-SKIP: contract sha256=$contract_hash"
    exit 0
fi

mv "$stage_directory/schema.json" "$output_directory/schema.json"
mv "$stage_directory/NetLift.swift" "$output_directory/NetLift.swift"
mv "$stage_directory/NetLift.kt" "$output_directory/NetLift.kt"
mv "$stage_directory/.contract-sha256" "$stamp"
echo "GENERATOR-RUN: contract sha256=$contract_hash"
