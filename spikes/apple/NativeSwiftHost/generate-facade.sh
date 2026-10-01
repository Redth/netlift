#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../../.." && pwd)
managed_project="$script_dir/managed/NetLift.ManagedBridge.csproj"
contract_source="$script_dir/managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
projection_project="$repo_root/src/NetLift.Projection/NetLift.Projection.csproj"
generated_dir="$script_dir/Generated"

dotnet build "$managed_project" -c Release -f net11.0
dotnet run --project "$projection_project" -c Release -- "$contract_source" "$generated_dir"
printf '%s\n' \
    'module NetLiftNativeHostABI {' \
    '    header "netlift.h"' \
    '    export *' \
    '}' > "$generated_dir/module.modulemap"
