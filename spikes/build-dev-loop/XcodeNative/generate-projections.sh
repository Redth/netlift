#!/usr/bin/env bash
set -euo pipefail

repo_root="${NETLIFT_REPOSITORY_ROOT:?NETLIFT_REPOSITORY_ROOT must point at the repository root}"
derived_dir="${DERIVED_FILE_DIR}/NetLiftProjection"
selected_contract="$SRCROOT/contract-selection.txt"
baseline_fixture="$SRCROOT/fixtures/ProjectionFixture.g.cs"
changed_fixture="$SRCROOT/fixtures/ProjectionFixture.ContractChanged.g.cs"
projection_project="$repo_root/src/NetLift.Projection/NetLift.Projection.csproj"
managed_artifacts="$DERIVED_FILE_DIR/.managed-artifacts"
staging_dir="$DERIVED_FILE_DIR/.NetLiftProjection-staging"
dotnet_path="$(command -v dotnet)"
dotnet_root="$(cd "$(dirname "$dotnet_path")" && pwd -P)"

case "$(cat "$selected_contract")" in
    baseline)
        fixture="$baseline_fixture"
        consumer="$SRCROOT/Consumers/NativeMain.baseline.swift"
        ;;
    contract-changed)
        fixture="$changed_fixture"
        consumer="$SRCROOT/Consumers/NativeMain.contract-changed.swift"
        ;;
    *)
        echo "Unsupported selected contract: $(cat "$selected_contract")" >&2
        exit 1
        ;;
esac

mkdir -p "$derived_dir"
input_hash="$(/bin/bash "$SRCROOT/hash-projection-inputs.sh" \
    "$selected_contract" \
    "$fixture" \
    "$consumer" \
    "$SRCROOT/generate-projections.sh" \
    "$SRCROOT/verify-projections.sh" \
    "$SRCROOT/include/NetLiftProjectionSupport.h" \
    "$repo_root/global.json" \
    "$projection_project" \
    "$repo_root/src/NetLift.Projection/Program.cs" \
    "$SRCROOT/hash-projection-inputs.sh")"
all_outputs_present=true
for source in NetLift.swift netlift.h abi.c NativeMain.swift projection-manifest.txt; do
    if [[ ! -s "$derived_dir/$source" ]]; then
        all_outputs_present=false
        break
    fi
done
if [[ "$all_outputs_present" == true ]] &&
    [[ "$(sed -n 's/^inputsSha256=//p' "$derived_dir/projection-manifest.txt")" == "$input_hash" ]]; then
    /bin/bash "$SRCROOT/verify-projections.sh"
    echo "XCODE-PROJECTION-SKIP: inputs and generated output hashes unchanged"
    exit 0
fi

rm -f \
    "$derived_dir/NetLift.swift" \
    "$derived_dir/netlift.h" \
    "$derived_dir/abi.c" \
    "$derived_dir/NativeMain.swift" \
    "$derived_dir/projection-manifest.txt"
rm -rf "$staging_dir"
mkdir -p "$staging_dir"

run_dotnet() {
    env -i \
        PATH="$PATH" \
        HOME="$DERIVED_FILE_DIR/.home" \
        TMPDIR="${TMPDIR:-/tmp}" \
        DOTNET_ROOT="$dotnet_root" \
        DOTNET_CLI_HOME="$DERIVED_FILE_DIR/.dotnet-home" \
        NUGET_PACKAGES="$DERIVED_FILE_DIR/.nuget" \
        dotnet "$@"
}

echo "XCODE-PROJECTION-RUN: NetLift.Projection -> $derived_dir"
run_dotnet build "$projection_project" \
    -c Release \
    --artifacts-path "$managed_artifacts" \
    --nologo
projection_dll="$managed_artifacts/bin/NetLift.Projection/release/NetLift.Projection.dll"
run_dotnet "$projection_dll" "$fixture" "$staging_dir"

expected_hash="$(sed -n 's/.*SchemaHash = "\([[:xdigit:]]*\)".*/\1/p' "$fixture")"
header_hash="$(sed -n 's/^#define LIFT_SCHEMA_HASH_HEX "\([[:xdigit:]]*\)"/\1/p' \
    "$staging_dir/netlift.h")"
if [[ -z "$expected_hash" || "$header_hash" != "$expected_hash" ]] ||
    [[ ! -s "$staging_dir/NetLift.swift" || ! -s "$staging_dir/netlift.h" ]]; then
    echo "Projection generated missing sources or a schema hash mismatch." >&2
    exit 1
fi
if ! grep -Fq 'struct Point' "$staging_dir/NetLift.swift"; then
    echo "Projection output is malformed: Point DTO is missing." >&2
    exit 1
fi
if [[ "$(cat "$selected_contract")" == "contract-changed" ]] &&
    ! grep -Fq 'struct Vector' "$staging_dir/NetLift.swift"; then
    echo "Projection output is malformed: selected Vector DTO is missing." >&2
    exit 1
fi

schema_bytes="$(printf '%s' "$expected_hash" | sed 's/../0x&, /g' | sed 's/, $//')"
cat > "$staging_dir/abi.c" <<EOF
#include <string.h>
#include "NetLiftProjectionSupport.h"

static const uint8_t expected_schema_hash[32] = { $schema_bytes };

const char *projection_fixture_schema_hash(void)
{
    return LIFT_SCHEMA_HASH_HEX;
}

void projection_fixture_make_config(lift_config *config)
{
    if (config == NULL)
        return;

    memset(config, 0, sizeof(*config));
    config->struct_size = (uint32_t)sizeof(*config);
    config->abi_version = LIFT_ABI_VERSION;
    memcpy(config->schema_hash, expected_schema_hash, sizeof(expected_schema_hash));
}

uint32_t projection_fixture_validate_config(const lift_config *config)
{
    if (config == NULL)
        return LIFT_STATUS_INVALID_ARGUMENT;
    if (config->struct_size != sizeof(*config) || config->abi_version != LIFT_ABI_VERSION)
        return LIFT_STATUS_VERSION_MISMATCH;
    if (memcmp(config->schema_hash, expected_schema_hash, sizeof(expected_schema_hash)) != 0)
        return LIFT_STATUS_SCHEMA_MISMATCH;
    return LIFT_STATUS_OK;
}
EOF
cp "$consumer" "$staging_dir/NativeMain.swift"

for source in NetLift.swift netlift.h abi.c NativeMain.swift; do
    if [[ ! -s "$staging_dir/$source" ]]; then
        echo "Projection staging is missing required source: $source" >&2
        exit 1
    fi
done
swift_hash="$(shasum -a 256 "$staging_dir/NetLift.swift" | awk '{ print $1 }')"
header_hash_bytes="$(shasum -a 256 "$staging_dir/netlift.h" | awk '{ print $1 }')"
c_hash="$(shasum -a 256 "$staging_dir/abi.c" | awk '{ print $1 }')"
main_hash="$(shasum -a 256 "$staging_dir/NativeMain.swift" | awk '{ print $1 }')"
printf 'schemaHash=%s\ninputsSha256=%s\nswiftSha256=%s\nheaderSha256=%s\ncSha256=%s\nmainSha256=%s\n' \
    "$expected_hash" "$input_hash" "$swift_hash" "$header_hash_bytes" "$c_hash" "$main_hash" \
    > "$staging_dir/projection-manifest.txt"

for source in NetLift.swift netlift.h abi.c NativeMain.swift projection-manifest.txt; do
    mv "$staging_dir/$source" "$derived_dir/$source"
done
echo "XCODE-PROJECTION-MANIFEST: schema sha256=$expected_hash"
/bin/bash "$SRCROOT/verify-projections.sh"
