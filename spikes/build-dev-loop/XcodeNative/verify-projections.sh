#!/usr/bin/env bash
set -euo pipefail

generated_dir="${DERIVED_FILE_DIR}/NetLiftProjection"
selected_contract="$SRCROOT/contract-selection.txt"
fixture_name="ProjectionFixture.g.cs"
case "$(cat "$selected_contract")" in
    baseline) ;;
    contract-changed) fixture_name="ProjectionFixture.ContractChanged.g.cs" ;;
    *)
        echo "Unsupported selected contract: $(cat "$selected_contract")" >&2
        exit 1
        ;;
esac
fixture="$SRCROOT/fixtures/$fixture_name"
expected_hash="$(sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$fixture")"
manifest="$generated_dir/projection-manifest.txt"
if [[ ! -s "$manifest" ]]; then
    echo "Generated projection manifest is missing; refusing to compile stale sources." >&2
    exit 1
fi
manifest_value() {
    sed -n "s/^$1=//p" "$manifest"
}

for file in NetLift.swift netlift.h abi.c NativeMain.swift; do
    if [[ ! -s "$generated_dir/$file" ]]; then
        echo "Generated projection source is missing: $generated_dir/$file" >&2
        exit 1
    fi
done
if [[ "$(manifest_value schemaHash)" != "$expected_hash" ]]; then
    echo "Generated manifest does not match selected contract hash." >&2
    exit 1
fi
for pair in \
    "swiftSha256:NetLift.swift" \
    "headerSha256:netlift.h" \
    "cSha256:abi.c" \
    "mainSha256:NativeMain.swift"; do
    key="${pair%%:*}"
    file="${pair#*:}"
    actual="$(shasum -a 256 "$generated_dir/$file" | awk '{ print $1 }')"
    if [[ "$actual" != "$(manifest_value "$key")" ]]; then
        echo "Generated source integrity mismatch for $file; refusing to compile stale or corrupted output." >&2
        exit 1
    fi
done
header_hash="$(sed -n 's/^#define LIFT_SCHEMA_HASH_HEX "\([[:xdigit:]]*\)"/\1/p' \
    "$generated_dir/netlift.h")"
if [[ "$header_hash" != "$expected_hash" ]]; then
    echo "Generated header schema hash does not match selected contract." >&2
    exit 1
fi
if ! grep -Fq 'struct Point' "$generated_dir/NetLift.swift"; then
    echo "Generated Swift surface is malformed: Point DTO is missing." >&2
    exit 1
fi
if [[ "$(cat "$selected_contract")" == "contract-changed" ]] &&
    ! grep -Fq 'struct Vector' "$generated_dir/NetLift.swift"; then
    echo "Generated Swift surface is malformed: Vector DTO is missing." >&2
    exit 1
fi
echo "XCODE-PROJECTION-VALIDATE: schema sha256=$expected_hash"
