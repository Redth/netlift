#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

bash tools/test-run-android-generated-facade-regressions.sh

if command -v zip >/dev/null && command -v unzip >/dev/null &&
    command -v python3 >/dev/null; then
    bash tools/test-inspect-mobile-package.sh
else
    echo "SKIP: mobile package auditor tests require zip, unzip and Python 3."
fi

if command -v python3 >/dev/null; then
    bash spikes/optional-negotiation/run.sh
else
    echo "SKIP: additive optional-field reference protocol requires Python 3."
fi

dotnet run --project samples/Orders.Smoke/Orders.Smoke.csproj -c Release --nologo
dotnet run --project tests/NetLift.Samples.AsyncBusiness.Smoke/NetLift.Samples.AsyncBusiness.Smoke.csproj -c Release --nologo
dotnet run --project tests/NetLift.Tests/NetLift.Tests.csproj -c Debug --nologo
dotnet run --project tests/NetLift.Negotiation.AsyncHost.Managed.Smoke/NetLift.Negotiation.AsyncHost.Managed.Smoke.csproj -c Release --nologo
dotnet build tests/NetLift.Fixture/NetLift.Fixture.csproj -c Release --nologo
dotnet run --project tests/NetLift.SchemaDiff.Tests/NetLift.SchemaDiff.Tests.csproj -c Release --nologo
dotnet run --project tests/NetLift.Negotiation.Tests/NetLift.Negotiation.Tests.csproj -c Release --nologo

generated="tests/NetLift.Fixture/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
projected="tests/NetLift.Fixture/obj/projected"
dotnet run --project src/NetLift.SchemaDiff/NetLift.SchemaDiff.csproj -c Release -- "$generated" "$generated" >/dev/null
dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- "$generated" "$projected"

if command -v clang >/dev/null && command -v swiftc >/dev/null; then
    clang -std=c11 -x c -fsyntax-only "$projected/netlift.h"
    swiftc -typecheck -parse-as-library "$projected/NetLift.swift"
else
    echo "SKIP: C/Swift projection compilation requires clang and swiftc."
fi

kotlin_source="tests/NetLift.Tests/bin/Debug/net11.0/projection-edge-cases/NetLift.kt"
if [[ ! -s "$kotlin_source" ]]; then
    echo "FAIL: the contract tests did not generate the Kotlin fixture: $kotlin_source" >&2
    exit 1
fi
stream_projection="$PWD/tests/NetLift.Tests/bin/Debug/net11.0/projection-stream-v4"
if [[ ! -s "$stream_projection/NetLift.kt" ]]; then
    echo "FAIL: the contract tests did not generate the v4 Kotlin stream fixture: $stream_projection/NetLift.kt" >&2
    exit 1
fi

native_host_generated="tests/NetLift.NativeHost.Managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
dotnet build tests/NetLift.NativeHost.Managed/NetLift.NativeHost.Managed.csproj -c Release --nologo
dotnet run --project src/NetLift.Projection/NetLift.Projection.csproj -c Release -- \
    "$native_host_generated" tests/NetLift.NativeHost.Managed/obj/projected

if [[ -n "${NETLIFT_GRADLE:-}" ]]; then
    NETLIFT_STREAM_PROJECTION="$stream_projection" "$NETLIFT_GRADLE" -p tests/NetLift.KotlinCompile run \
        --offline --no-daemon --console=plain
    "$NETLIFT_GRADLE" -p tests/NetLift.KotlinTypedReplyCompile run \
        --offline --no-daemon --console=plain
    "$NETLIFT_GRADLE" -p tests/NetLift.KotlinGeneratedServiceFacadeCompile run \
        --offline --no-daemon --console=plain
elif command -v gradle >/dev/null; then
    NETLIFT_STREAM_PROJECTION="$stream_projection" gradle -p tests/NetLift.KotlinCompile run \
        --offline --no-daemon --console=plain
    gradle -p tests/NetLift.KotlinTypedReplyCompile run \
        --offline --no-daemon --console=plain
    gradle -p tests/NetLift.KotlinGeneratedServiceFacadeCompile run \
        --offline --no-daemon --console=plain
else
    echo "SKIP: Kotlin projection compilation and smoke tests require Gradle (set NETLIFT_GRADLE)."
fi

dotnet run --project tests/NetLift.NativeHost.Managed.Tests/NetLift.NativeHost.Managed.Tests.csproj -c Release --nologo

if [[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]]; then
    if command -v swiftc >/dev/null; then
        bash spikes/build-dev-loop/run-incremental-check.sh
        bash tests/NetLift.Tests/run-swift-callback-lifecycle-v2-tests.sh
        if [[ -n "${NETLIFT_GRADLE:-}" ]] || command -v gradle >/dev/null; then
            bash tests/NetLift.Tests/run-schema-v5-codec-tests.sh
            bash tests/NetLift.Tests/run-schema-v5-host-codec-tests.sh
            bash tests/NetLift.Tests/run-schema-v5-async-host-codec-tests.sh
        else
            echo "SKIP: schema-v5 Swift/Kotlin sync/async codec tests require Gradle (set NETLIFT_GRADLE)."
        fi
    else
        echo "SKIP: incremental native build graph and Swift V2 lifecycle tests require swiftc."
    fi
    bash spikes/desktop-host/run.sh
    bash tests/NetLift.NativeHost/run.sh
    bash tests/NetLift.Negotiation.NativeHost/run.sh
    NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS="${NETLIFT_NATIVEHOST_ASYNC_ARTIFACTS:-$PWD/tests/NetLift.Negotiation.NativeHost.AsyncResult/native-artifacts}" \
        bash tests/NetLift.Negotiation.NativeHost.AsyncResult/run.sh
    NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS="${NETLIFT_NATIVEHOST_ASYNC_RUNTIME_ARTIFACTS:-$PWD/tests/NetLift.Negotiation.NativeHost.AsyncRuntime/native-artifacts}" \
        bash tests/NetLift.Negotiation.NativeHost.AsyncRuntime/run.sh
    dotnet run --project tests/NetLift.StreamHost.Managed/NetLift.StreamHost.Managed.csproj -c Release --nologo
    bash tests/NetLift.StreamHost.Managed/run-native.sh
    typed_generated="tests/NetLift.NativeHost.TypedReply.Managed/obj/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
    if typed_diff="$(dotnet run --project src/NetLift.SchemaDiff/NetLift.SchemaDiff.csproj -c Release -- "$typed_generated" "$typed_generated")"; then
        echo "FAIL: schema-v3 diff was incorrectly classified as compatible." >&2
        exit 1
    else
        diff_status=$?
        if [[ "$diff_status" -ne 2 ]] ||
            ! grep -Fq '"classification": "unsupported"' <<< "$typed_diff" ||
            ! grep -Fq '"schema-version-unsupported"' <<< "$typed_diff"; then
            printf 'FAIL: schema-v3 diff did not report an unsupported version (exit %s):\n%s\n' \
                "$diff_status" "$typed_diff" >&2
            exit 1
        fi
    fi
    bash spikes/event-streams/run.sh
    bash spikes/optional-negotiation/run-managed.sh
    bash spikes/buffer-ownership/run.sh
    bash spikes/buffer-ownership/run-managed.sh
    if [[ -d /Applications/Xcode_26.6.app/Contents/Developer ]] && command -v xcrun >/dev/null; then
        sh spikes/apple/NativeSwiftHost/run-lifecycle-test.sh
        sh spikes/apple/NativeSwiftHost/typed/generate-typed-facade.sh
        sh spikes/apple/NativeSwiftHost/typed/tests/run-callback-lifecycle-v2-test.sh
        sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-adapter-test.sh
        sh spikes/apple/NativeSwiftHost/typed/tests/run-v2-accessor-preflight-test.sh
        sh spikes/apple/NativeSwiftHost/typed/verify-v2-source-contract.sh
    else
        echo "SKIP: Apple generated-client and V2 host lifecycle harnesses require Xcode 26.6."
    fi
    if command -v swift >/dev/null; then
        bash spikes/macos-native/run.sh --bundled-smoke
        NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
            bash tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
        NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS=1 \
            NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
            NETLIFT_V5_SWIFT_CALLER_ARTIFACTS="${NETLIFT_V5_SWIFT_CALLER_ASYNC_ARTIFACTS:-$PWD/tests/NetLift.Negotiation.NativeHost.SwiftCaller/async-artifacts}" \
            bash tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
    else
        echo "SKIP: native Swift/CoreCLR controls require SwiftPM."
    fi
else
    echo "SKIP: native CoreCLR host controls require macOS arm64."
fi

echo "Available NetLift local checks passed; mobile device gates remain separate."
