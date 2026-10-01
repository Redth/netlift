#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 4 ]]; then
    echo "Usage: stage-v5-codec.sh <old|new> <swift|kotlin> <output-directory> <variant-key>" >&2
    exit 2
fi

direction="$1"
language="$2"
output_dir="$3"
variant_key="$4"
repo_root="$(cd "$(dirname "$0")/../../.." && pwd -P)"
spike_root="$repo_root/spikes/build-dev-loop"
artifact_root="${NETLIFT_NATIVE_GRAPH_ARTIFACTS:?NETLIFT_NATIVE_GRAPH_ARTIFACTS must point to an isolated root}"
dotnet_home="$artifact_root/dotnet-home"
nuget_packages="$artifact_root/nuget-packages"
projection="$artifact_root/projection-artifacts/bin/NetLift.Projection/release/NetLift.Projection.dll"
projection_inputs="$artifact_root/inputs/projection-source.sha256"
contract="$artifact_root/inputs/contracts/$direction/LiftContract.g.cs"
lock="$artifact_root/inputs/codec-schema.lock"
stage_helper="$spike_root/V5NativeConsumer/stage-v5-codec.py"

case "$direction:$language" in
    old:swift|old:kotlin|new:swift|new:kotlin) ;;
    *)
        echo "Unsupported v5 direction/language: $direction/$language" >&2
        exit 2
        ;;
esac
case "$variant_key" in
    *[!A-Za-z0-9_.-]*|'')
        echo "Variant key may contain only letters, digits, dot, underscore, and dash." >&2
        exit 2
        ;;
esac
if [[ ! -s "$projection" || ! -s "$projection_inputs" || ! -s "$contract" || ! -s "$lock" ]]; then
    echo "Required isolated v5 inputs are missing under $artifact_root." >&2
    exit 1
fi

staging_root="$artifact_root/staging"
mkdir -p "$staging_root"
temporary="$(mktemp -d "$staging_root/$variant_key.XXXXXX")"
emitted="$temporary/emitted"
mkdir -p "$emitted"

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$status" -ne 0 ]]; then
        rm -rf "$output_dir"
    fi
    rm -rf "$temporary"
    exit "$status"
}
trap cleanup EXIT

dotnet_root="$(cd "$(dirname "$(command -v dotnet)")" && pwd -P)"
env -i \
    PATH="$PATH" \
    HOME="$artifact_root/home" \
    TMPDIR="${TMPDIR:-$staging_root}" \
    DOTNET_ROOT="$dotnet_root" \
    DOTNET_CLI_HOME="$dotnet_home" \
    NUGET_PACKAGES="$nuget_packages" \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    dotnet "$projection" "$contract" "$emitted" --emit-schema-v5-host-codecs

python3 "$stage_helper" "$lock" "$direction" "$language" "$contract" \
    "$projection_inputs" "$emitted" "$output_dir"
