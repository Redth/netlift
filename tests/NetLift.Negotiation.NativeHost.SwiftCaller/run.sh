#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCOPE="$ROOT/tests/NetLift.Negotiation.NativeHost.SwiftCaller"
ARTIFACT_ROOT="${NETLIFT_V5_SWIFT_CALLER_ARTIFACTS:-$SCOPE/native-artifacts}"
if [[ "$ARTIFACT_ROOT" != /* ]]; then
    ARTIFACT_ROOT="$SCOPE/$ARTIFACT_ROOT"
fi
REFERENCE_HOST="$ROOT/tests/NetLift.Negotiation.NativeHost"
USE_GENERATED_CODECS="${NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS:-0}"
USE_ASYNC_RESULTS="${NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS:-0}"

if [[ "$USE_ASYNC_RESULTS" == "1" &&
      -z "${NETLIFT_V5_SWIFT_CALLER_ARTIFACTS:-}" ]]; then
    echo "Set NETLIFT_V5_SWIFT_CALLER_ARTIFACTS to an isolated artifact directory for AsyncCallbackV1." >&2
    exit 2
fi
if [[ "$USE_GENERATED_CODECS" != "0" && "$USE_GENERATED_CODECS" != "1" ]]; then
    echo "NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS must be 0 or 1." >&2
    exit 2
fi
if [[ "$USE_ASYNC_RESULTS" != "0" && "$USE_ASYNC_RESULTS" != "1" ]]; then
    echo "NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS must be 0 or 1." >&2
    exit 2
fi
mkdir -p "$ARTIFACT_ROOT"
if [[ "$USE_ASYNC_RESULTS" == "1" ]]; then
    ASYNC_HOST="$ROOT/tests/NetLift.Negotiation.NativeHost.AsyncResult"
    DOTNET_ARTIFACTS="$ARTIFACT_ROOT/native-host/dotnet-artifacts"
    OLD_PROJECT="$ASYNC_HOST/Managed.Old/NetLift.Negotiation.NativeHost.Async.Managed.Old.csproj"
    NEW_PROJECT="$ASYNC_HOST/Managed.New/NetLift.Negotiation.NativeHost.Async.Managed.New.csproj"
    OLD_NAME="NetLift.Negotiation.NativeHost.Async.Managed.Old"
    NEW_NAME="NetLift.Negotiation.NativeHost.Async.Managed.New"
    OLD_OUTPUT="$DOTNET_ARTIFACTS/bin/$OLD_NAME/release"
    NEW_OUTPUT="$DOTNET_ARTIFACTS/bin/$NEW_NAME/release"
    BUILD_ARGS=(--nologo --artifacts-path "$DOTNET_ARTIFACTS")

    dotnet build "$OLD_PROJECT" -c Release "${BUILD_ARGS[@]}"
    dotnet build "$NEW_PROJECT" -c Release "${BUILD_ARGS[@]}"

    find_contract_source() {
        local generated_root="$1"
        local result
        result="$(find "$generated_root" -type f \
            -path '*/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs' -print -quit)"
        if [[ -z "$result" ]]; then
            echo "Could not find generated LiftContract.g.cs under $generated_root." >&2
            exit 1
        fi
        printf '%s\n' "$result"
    }

    OLD_CONTRACT="$(find_contract_source \
        "$DOTNET_ARTIFACTS/obj/NetLift.Negotiation.AsyncHost.Managed.Old")"
    NEW_CONTRACT="$(find_contract_source \
        "$DOTNET_ARTIFACTS/obj/NetLift.Negotiation.AsyncHost.Managed.New")"
    python3 "$ROOT/spikes/build-dev-loop/stage-v5-peer-pin.py" \
        "$SCOPE/Fixtures/async-peer-provenance.lock" \
        "$OLD_CONTRACT" \
        "$NEW_CONTRACT" \
        "$ARTIFACT_ROOT/native-host/peer-pin"
else
    NETLIFT_NATIVEHOST_ARTIFACTS="$ARTIFACT_ROOT/native-host" \
        bash "$REFERENCE_HOST/run.sh"
    OLD_NAME="NetLift.Negotiation.NativeHost.Managed.Old"
    NEW_NAME="NetLift.Negotiation.NativeHost.Managed.New"
    OLD_OUTPUT="$ARTIFACT_ROOT/native-host/dotnet-artifacts/bin/$OLD_NAME/release"
    NEW_OUTPUT="$ARTIFACT_ROOT/native-host/dotnet-artifacts/bin/$NEW_NAME/release"
fi

DOTNET_ROOT="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
RUNTIME_VERSION="$(dotnet --list-runtimes | awk \
    '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\./ { version = $2 } END { print version }')"
if [[ -z "$RUNTIME_VERSION" ]]; then
    echo "Could not find an installed .NET 11 runtime." >&2
    exit 1
fi
NATIVE_HOST_PACK="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$RUNTIME_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$NATIVE_HOST_PACK/libnethost.dylib" ]]; then
    echo "Could not find the osx-arm64 nethost pack for runtime $RUNTIME_VERSION." >&2
    exit 1
fi

export NETLIFT_V5_REFERENCE_HOST="$REFERENCE_HOST"
export NETLIFT_V5_NATIVE_HOST_PACK="$NATIVE_HOST_PACK"
export NETLIFT_V5_PIN_HEADER_DIRECTORY="$ARTIFACT_ROOT/native-host/peer-pin"
export NETLIFT_V5_DOTNET_ROOT="$DOTNET_ROOT"
export NETLIFT_V5_OLD_ASSEMBLY="$OLD_OUTPUT/$OLD_NAME.dll"
export NETLIFT_V5_OLD_RUNTIMECONFIG="$OLD_OUTPUT/$OLD_NAME.runtimeconfig.json"
export NETLIFT_V5_NEW_ASSEMBLY="$NEW_OUTPUT/$NEW_NAME.dll"
export NETLIFT_V5_NEW_RUNTIMECONFIG="$NEW_OUTPUT/$NEW_NAME.runtimeconfig.json"

if [[ "$USE_GENERATED_CODECS" == "1" ]]; then
    find_contract_source() {
        local generated_root="$1"
        local result
        result="$(find "$generated_root" -type f \
            -path '*/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs' -print -quit)"
        if [[ -z "$result" ]]; then
            echo "Could not find generated LiftContract.g.cs under $generated_root." >&2
            exit 1
        fi
        printf '%s\n' "$result"
    }

    if [[ "$USE_ASYNC_RESULTS" != "1" ]]; then
        OLD_CONTRACT="$(find_contract_source "$ARTIFACT_ROOT/native-host/dotnet-artifacts/obj/NetLift.Negotiation.NativeHost.Managed.Old")"
        NEW_CONTRACT="$(find_contract_source "$ARTIFACT_ROOT/native-host/dotnet-artifacts/obj/NetLift.Negotiation.NativeHost.Managed.New")"
    fi
    dotnet build "$ROOT/src/NetLift.Projection/NetLift.Projection.csproj" \
        -c Release --artifacts-path "$ARTIFACT_ROOT/projection-dotnet" --nologo
    PROJECTION_DLL="$ARTIFACT_ROOT/projection-dotnet/bin/NetLift.Projection/release/NetLift.Projection.dll"
    if [[ "$USE_ASYNC_RESULTS" == "1" ]]; then
        CODEC_OPTION=--emit-schema-v5-async-host-codecs
    else
        CODEC_OPTION=--emit-schema-v5-host-codecs
    fi
    dotnet "$PROJECTION_DLL" "$OLD_CONTRACT" "$ARTIFACT_ROOT/generated-codecs/old" "$CODEC_OPTION"
    dotnet "$PROJECTION_DLL" "$NEW_CONTRACT" "$ARTIFACT_ROOT/generated-codecs/new" "$CODEC_OPTION"
fi

PACKAGE_ROOT="$ARTIFACT_ROOT/swift-package"
mkdir -p \
    "$PACKAGE_ROOT/Sources/NetLiftV5NativeBridge/include" \
    "$PACKAGE_ROOT/Sources/NetLiftV5Host" \
    "$PACKAGE_ROOT/Sources/NetLiftV5OldCodec" \
    "$PACKAGE_ROOT/Sources/NetLiftV5NewCodec" \
    "$PACKAGE_ROOT/Tests/NetLiftV5HostTests"
cp "$SCOPE/Package.swift" "$PACKAGE_ROOT/Package.swift"
cp "$SCOPE/Sources/NetLiftV5NativeBridge/netlift_v5_native_bridge.c" \
    "$PACKAGE_ROOT/Sources/NetLiftV5NativeBridge/"
cp "$SCOPE/Sources/NetLiftV5NativeBridge/include/netlift_v5_native_bridge.h" \
    "$PACKAGE_ROOT/Sources/NetLiftV5NativeBridge/include/"
cp "$SCOPE/Sources/NetLiftV5Host/NetLiftV5Host.swift" \
    "$PACKAGE_ROOT/Sources/NetLiftV5Host/"
cp "$SCOPE/Sources/NetLiftV5Host/ManagedResultSchemaContext.swift" \
    "$PACKAGE_ROOT/Sources/NetLiftV5Host/"
cp "$SCOPE/Sources/NetLiftV5OldCodec/ModuleMarker.swift" \
    "$PACKAGE_ROOT/Sources/NetLiftV5OldCodec/"
cp "$SCOPE/Sources/NetLiftV5NewCodec/ModuleMarker.swift" \
    "$PACKAGE_ROOT/Sources/NetLiftV5NewCodec/"
cp "$SCOPE/Tests/NetLiftV5HostTests/NetLiftV5HostIntegrationTests.swift" \
    "$PACKAGE_ROOT/Tests/NetLiftV5HostTests/"

if [[ "$USE_GENERATED_CODECS" == "1" ]]; then
    copy_generated_swift() {
        local generated_root="$1"
        local destination="$2"
        local copied=0
        local source
        while IFS= read -r -d '' source; do
            cp "$source" "$destination/"
            copied=$((copied + 1))
        done < <(find "$generated_root" -type f -name '*.swift' -print0)
        if [[ "$copied" -eq 0 ]]; then
            echo "No generated Swift files found under $generated_root." >&2
            exit 1
        fi
    }

    copy_generated_swift "$ARTIFACT_ROOT/generated-codecs/old" \
        "$PACKAGE_ROOT/Sources/NetLiftV5OldCodec"
    copy_generated_swift "$ARTIFACT_ROOT/generated-codecs/new" \
        "$PACKAGE_ROOT/Sources/NetLiftV5NewCodec"
fi

export NETLIFT_V5_ENABLE_HOST_CODECS="$USE_GENERATED_CODECS"
export NETLIFT_V5_ENABLE_ASYNC_RESULTS="$USE_ASYNC_RESULTS"
swift test \
    --package-path "$PACKAGE_ROOT" \
    --scratch-path "$ARTIFACT_ROOT/swift-build"
