#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../../../.." && pwd)
managed_project="$script_dir/TypedManagedBridge.csproj"
contract_source="$script_dir/.artifacts/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
projection_project="$repo_root/src/NetLift.Projection/NetLift.Projection.csproj"
generated_dir="$script_dir/GeneratedTyped"
v1_projection_dir="$script_dir/.artifacts/generated-v1"
artifacts_path="$script_dir/.artifacts/dotnet-artifacts"
projection_artifacts_path="$script_dir/.artifacts/projection-artifacts"

dotnet build "$managed_project" \
    -c Release \
    -f net11.0 \
    --artifacts-path "$artifacts_path"
dotnet run --project "$projection_project" \
    -c Release \
    --artifacts-path "$projection_artifacts_path" \
    -- "$contract_source" "$v1_projection_dir"
dotnet run --project "$projection_project" \
    -c Release \
    --artifacts-path "$projection_artifacts_path" \
    -- "$contract_source" "$generated_dir" --swift-native-callback-lifecycle-v2
# The opt-in V2 projection replaces the V1 async owner, so retain its unique
# client implementation alongside the generated V2 registration in this app.
if ! grep -Fq 'private final class NetLiftAsyncOperation_2' "$v1_projection_dir/NetLift.swift"; then
    echo "The V1 async client implementation was not generated." >&2
    exit 1
fi
{
    printf '\n#if canImport(NetLiftNativeHostABI)\n'
    sed -n '/^private final class NetLiftAsyncOperation_2/,$p' \
        "$v1_projection_dir/NetLift.swift"
} >> "$generated_dir/NetLift.swift"
sed 's|#include "GeneratedTyped/netlift.h"|#include "netlift.h"|' \
    "$script_dir/NetLiftCallbackLifecycleV2.h" \
    > "$generated_dir/NetLiftCallbackLifecycleV2.h"
printf '%s\n' \
    'module NetLiftNativeHostABI {' \
    '    header "NetLiftCallbackLifecycleV2.h"' \
    '    export *' \
    '}' > "$generated_dir/module.modulemap"
