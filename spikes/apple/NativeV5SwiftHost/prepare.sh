#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../../.." && pwd)
artifact_root=${NETLIFT_V5_IOS_ARTIFACTS:-"$script_dir/.artifacts"}
mkdir -p "$artifact_root"
artifact_root=$(CDPATH= cd -- "$artifact_root" && pwd)
export NETLIFT_V5_IOS_ARTIFACTS="$artifact_root"
async_results=${NETLIFT_V5_IOS_ASYNC_RESULTS:-0}
async_execution=${NETLIFT_V5_IOS_ASYNC_EXECUTION:-0}
async_direction=${NETLIFT_V5_IOS_ASYNC_DIRECTION:-old}
if [ "$async_results" = "0" ] && [ "$async_execution" = "1" ]; then
    case "$async_direction" in
        old|new) ;;
        *)
            echo "NETLIFT_V5_IOS_ASYNC_DIRECTION must be old or new." >&2
            exit 2
            ;;
    esac
    managed_project="$script_dir/managed-async-execution/NetLift.V5.AppHost.csproj"
    managed_artifacts="$artifact_root/managed-async-execution"
    old_managed_name="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.Old"
    new_managed_name="NetLift.Negotiation.NativeHost.AsyncRuntime.Managed.New"
    old_contract_project="Orders.AsyncBusiness"
    new_contract_project="NetLift.Negotiation.AsyncHost.Managed.New"
    pin_lock="$repo_root/tests/NetLift.Negotiation.NativeHost.SwiftCaller/Fixtures/async-peer-provenance.lock"
    codec_option=--emit-schema-v5-async-host-codecs
elif [ "$async_results" = "1" ] && [ "$async_execution" = "0" ]; then
    managed_project="$script_dir/managed-async/NetLift.V5.AppHost.csproj"
    managed_artifacts="$artifact_root/managed-async"
    old_managed_name="NetLift.Negotiation.NativeHost.Async.Managed.Old"
    new_managed_name="NetLift.Negotiation.NativeHost.Async.Managed.New"
    old_contract_project="NetLift.Negotiation.AsyncHost.Managed.Old"
    new_contract_project="NetLift.Negotiation.AsyncHost.Managed.New"
    pin_lock="$repo_root/tests/NetLift.Negotiation.NativeHost.SwiftCaller/Fixtures/async-peer-provenance.lock"
    codec_option=--emit-schema-v5-async-host-codecs
elif [ "$async_results" = "0" ] && [ "$async_execution" = "0" ]; then
    managed_project="$script_dir/managed/NetLift.V5.AppHost.csproj"
    managed_artifacts="$artifact_root/managed"
    old_managed_name="NetLift.Negotiation.NativeHost.Managed.Old"
    new_managed_name="NetLift.Negotiation.NativeHost.Managed.New"
    old_contract_project="$old_managed_name"
    new_contract_project="$new_managed_name"
    pin_lock="$repo_root/spikes/build-dev-loop/NativeHost/peer-provenance.lock"
    codec_option=--emit-schema-v5-host-codecs
else
    echo "Select exactly one of sync, NETLIFT_V5_IOS_ASYNC_RESULTS=1, or NETLIFT_V5_IOS_ASYNC_EXECUTION=1." >&2
    exit 2
fi
projection_artifacts="$artifact_root/projection"
generated_root="$artifact_root/generated-codecs"
package_root="$artifact_root/swift-package"

dotnet build "$managed_project" \
    -c Release \
    --artifacts-path "$managed_artifacts" \
    --nologo
dotnet build "$repo_root/src/NetLift.Projection/NetLift.Projection.csproj" \
    -c Release \
    --artifacts-path "$projection_artifacts" \
    --nologo

old_contract=$(find "$managed_artifacts/obj/$old_contract_project" \
    -type f -path '*/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs' -print -quit)
new_contract=$(find "$managed_artifacts/obj/$new_contract_project" \
    -type f -path '*/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs' -print -quit)
projection="$projection_artifacts/bin/NetLift.Projection/release/NetLift.Projection.dll"
if [ -z "$old_contract" ] || [ -z "$new_contract" ] || [ ! -s "$projection" ]; then
    echo "Missing generated managed contract or NetLift.Projection output." >&2
    exit 1
fi

mkdir -p "$generated_root/old" "$generated_root/new"
dotnet "$projection" "$old_contract" "$generated_root/old" "$codec_option"
dotnet "$projection" "$new_contract" "$generated_root/new" "$codec_option"
python3 "$repo_root/spikes/build-dev-loop/stage-v5-peer-pin.py" \
    "$pin_lock" "$old_contract" "$new_contract" "$artifact_root/peer-pin"

python3 - "$artifact_root/peer-pin/peer_provenance.manifest.json" \
    "$package_root/Sources/NetLiftV5Host/NetLiftV5BuildPins.swift" <<'PY'
import json
import sys
from pathlib import Path

manifest = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
old_hash = manifest["oldPeer"]["schemaHash"]
new_hash = manifest["newPeer"]["schemaHash"]
for value in (old_hash, new_hash):
    if len(value) != 64 or any(character not in "0123456789abcdef" for character in value):
        raise SystemExit("staged managed schema pin is not a lowercase SHA-256 digest")
Path(sys.argv[2]).parent.mkdir(parents=True, exist_ok=True)
Path(sys.argv[2]).write_text(
    "public enum NetLiftV5BuildPins {\n"
    f'    public static let oldManagedSchemaHash = "{old_hash}"\n'
    f'    public static let newManagedSchemaHash = "{new_hash}"\n'
    "}\n",
    encoding="utf-8")
PY

mkdir -p \
    "$package_root/Sources/NetLiftV5NativeBridge/include" \
    "$package_root/Sources/NetLiftV5Host" \
    "$package_root/Generated/Old" \
    "$package_root/Generated/New"
cp "$script_dir/Package.swift" "$package_root/Package.swift"
cp "$script_dir/Sources/NetLiftV5NativeBridge/netlift_v5_ios_bridge.m" \
    "$package_root/Sources/NetLiftV5NativeBridge/"
cp "$script_dir/Sources/NetLiftV5NativeBridge/include/netlift_v5_ios_bridge.h" \
    "$script_dir/Sources/NetLiftV5NativeBridge/include/module.modulemap" \
    "$package_root/Sources/NetLiftV5NativeBridge/include/"
cp "$script_dir/Sources/NetLiftV5Host/NetLiftV5Host.swift" \
    "$package_root/Sources/NetLiftV5Host/"
cp "$script_dir/Sources/NetLiftV5Host/ManagedResultSchemaContext.swift" \
    "$package_root/Sources/NetLiftV5Host/"

if [ "$async_execution" = "1" ]; then
    async_runtime_dir="$repo_root/tests/NetLift.Negotiation.NativeHost.AsyncRuntime"
    native_host_dir="$repo_root/tests/NetLift.Negotiation.NativeHost"
    typed_host_dir="$repo_root/spikes/apple/NativeSwiftHost/typed"
    native_core_dir="$repo_root/src/NetLift.NativeHost.V5"
    native_bridge_root="$package_root/Sources/NetLiftV5NativeBridge"
    native_core_root="$native_bridge_root/NativeHostV5Core"
    async_test_root="$native_bridge_root/AsyncRuntimeV2/TestOnly"
    mkdir -p "$native_core_root" "$async_test_root/GeneratedTyped"
    rm -f \
        "$async_test_root/CallbackLifecycleV2Adapter.h" \
        "$async_test_root/CallbackLifecycleV2State.c" \
        "$async_test_root/CallbackLifecycleV2State.h" \
        "$async_test_root/CallbackLifecycleV2Accessor.c" \
        "$async_test_root/CoreCLRHost.h"
    cp "$script_dir/Sources/NetLiftV5NativeBridge/AsyncExecutionHost.c" \
        "$script_dir/Sources/NetLiftV5NativeBridge/AppleAsyncV2Provider.c" \
        "$script_dir/Sources/NetLiftV5NativeBridge/AsyncV2ProviderCxxAbiCheck.cpp" \
        "$native_bridge_root/"
    cp "$native_core_dir/async_v2_core.c" \
        "$native_core_dir/async_v2_lifecycle.c" \
        "$native_core_dir/async_v2_accessor.c" \
        "$native_core_dir/async_v2_lifecycle.h" \
        "$native_core_dir/async_v2_accessor.h" \
        "$native_core_dir/async_v2_provider.h" \
        "$native_core_dir/async_v2_private_abi.h" \
        "$native_core_root/"
    cp "$async_runtime_dir/runtime_host.h" \
        "$native_host_dir/compatible_start_v1.h" \
        "$typed_host_dir/NetLiftCallbackLifecycleV2.h" \
        "$async_test_root/"
    python3 - "$typed_host_dir/GeneratedTyped/netlift.h" \
        "$async_test_root/netlift.h" \
        "$artifact_root/peer-pin/peer_provenance.manifest.json" \
        "$async_direction" \
        "$generated_root/$async_direction" <<'PY'
import json
import re
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text(encoding="utf-8")
manifest = json.loads(Path(sys.argv[3]).read_text(encoding="utf-8"))
schema_hash = manifest[f'{sys.argv[4]}Peer']["schemaHash"]
codec_sources = sorted(Path(sys.argv[5]).glob("*.swift"))
canonical_json = None
for codec_source in codec_sources:
    text = codec_source.read_text(encoding="utf-8")
    match = re.search(
        r'public static let canonicalJson = ("(?:\\.|[^"\\])*")',
        text)
    if match is not None:
        canonical_json = json.loads(match.group(1))
        break
if canonical_json is None:
    raise SystemExit("Generated direction has no canonical schema JSON.")
schema = json.loads(canonical_json)["schema"]
services = [
    service for service in schema["nativeServices"]
    if service.get("name") == "Confirmation"
]
if len(services) != 1 or sum(
        method.get("name") == "ConfirmAsync" for method in services[0]["methods"]) != 1:
    raise SystemExit("Generated direction must contain exactly one Confirmation.ConfirmAsync service.")
native_service_id = services[0]["id"]
if not isinstance(native_service_id, int) or native_service_id <= 0:
    raise SystemExit("Confirmation service ID must be a positive integer.")
pattern = re.compile(r'(?m)^#define LIFT_SCHEMA_HASH_HEX "[0-9a-f]{64}"$')
patched, replacements = pattern.subn(
    f'#define LIFT_SCHEMA_HASH_HEX "{schema_hash}"', source)
if replacements != 1:
    raise SystemExit("Expected exactly one generated LIFT_SCHEMA_HASH_HEX definition.")
if "NETLIFT_IOS_NATIVE_CONFIRMATION_SERVICE_ID" in patched:
    raise SystemExit("Apple confirmation service ID macro is already present in the staged header.")
patched += (
    f"\n#define NETLIFT_IOS_NATIVE_CONFIRMATION_SERVICE_ID {native_service_id}\n")
Path(sys.argv[2]).write_text(patched, encoding="utf-8")
PY
    cp "$async_test_root/netlift.h" \
        "$async_test_root/GeneratedTyped/netlift.h"
    if grep -Eq 'AsyncRuntimeV2/TestOnly/CallbackLifecycleV2(State|Accessor)[.]c' \
        "$package_root/Package.swift"; then
        echo "The Apple async package still selects the legacy test lifecycle/accessor." >&2
        exit 1
    fi
    if find "$async_test_root" -maxdepth 1 -type f \
        \( -name 'CallbackLifecycleV2State.c' -o -name 'CallbackLifecycleV2Accessor.c' \) \
        -print | grep -q .; then
        echo "A legacy Apple test lifecycle/accessor source remains staged." >&2
        exit 1
    fi
    printf 'Staged source-owned Async V2 core/lifecycle/accessor with Apple test-only CoreCLR provider inputs for %s-managed direction.\n' \
        "$async_direction"
    grep -E 'NativeHostV5Core/async_v2_(core|lifecycle|accessor)[.]c' \
        "$package_root/Package.swift"
    shasum -a 256 \
        "$native_core_root/async_v2_core.c" \
        "$native_core_root/async_v2_lifecycle.c" \
        "$native_core_root/async_v2_accessor.c" \
        "$native_core_root/async_v2_lifecycle.h" \
        "$native_core_root/async_v2_accessor.h" \
        "$native_core_root/async_v2_provider.h" \
        "$native_core_root/async_v2_private_abi.h" \
        "$native_bridge_root/AppleAsyncV2Provider.c" \
        "$native_bridge_root/AsyncExecutionHost.c"
fi

old_codec_count=$(find "$generated_root/old" -type f -name '*.swift' | wc -l | tr -d ' ')
new_codec_count=$(find "$generated_root/new" -type f -name '*.swift' | wc -l | tr -d ' ')
if [ "$old_codec_count" -eq 0 ] || [ "$new_codec_count" -eq 0 ]; then
    echo "No generated Swift codec source found for one or both fixture schemas." >&2
    exit 1
fi
find "$generated_root/old" -type f -name '*.swift' -exec cp '{}' "$package_root/Generated/Old/" \;
find "$generated_root/new" -type f -name '*.swift' -exec cp '{}' "$package_root/Generated/New/" \;
printf 'Prepared managed fixtures, generated codecs, and pinned SwiftPM inputs at %s\n' "$artifact_root"
