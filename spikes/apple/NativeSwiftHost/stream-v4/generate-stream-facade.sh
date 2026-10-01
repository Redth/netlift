#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../../../.." && pwd)
managed_project="$script_dir/managed/NetLift.ManagedBridge.StreamV4.csproj"
contract_source="$script_dir/managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
projection_project="$repo_root/src/NetLift.Projection/NetLift.Projection.csproj"
generated_dir="$script_dir/Generated"

dotnet build "$managed_project" -c Release -f net11.0
dotnet run --project "$projection_project" -c Release -- "$contract_source" "$generated_dir"
# Keep the projection's generic transport declarations unique beside the v2 Swift source.
sed \
    -e 's/LiftProjectionError/LiftStreamV4ProjectionError/g' \
    -e 's/LiftTransport/LiftStreamV4Transport/g' \
    "$generated_dir/NetLift.swift" > "$generated_dir/NetLift.StreamV4.swift"
rm "$generated_dir/NetLift.swift"
