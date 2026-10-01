#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPIKE_ROOT="$REPO_ROOT/spikes/native-hostfxr-streamed-route"
if [[ -z "${NETLIFT_HOSTFXR_STREAMED_ROUTE_ARTIFACTS:-}" ]]; then
    printf 'Set NETLIFT_HOSTFXR_STREAMED_ROUTE_ARTIFACTS to a session-local artifact directory.\n' >&2
    exit 2
fi
case "$NETLIFT_HOSTFXR_STREAMED_ROUTE_ARTIFACTS" in
    /*) ;;
    *)
        printf 'Artifact root must be an absolute path.\n' >&2
        exit 2
        ;;
esac
case "$NETLIFT_HOSTFXR_STREAMED_ROUTE_ARTIFACTS" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf 'Artifact root must not be inside shared system temp.\n' >&2
        exit 2
        ;;
esac

mkdir -p "$NETLIFT_HOSTFXR_STREAMED_ROUTE_ARTIFACTS"
ARTIFACT_ROOT="$(cd "$NETLIFT_HOSTFXR_STREAMED_ROUTE_ARTIFACTS" && pwd -P)"
case "$ARTIFACT_ROOT" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf 'Artifact root must not resolve inside shared system temp.\n' >&2
        exit 2
        ;;
esac
case "$ARTIFACT_ROOT" in
    */.copilot/session-state/*/files/*) ;;
    *)
        printf 'Artifact root must be under the Copilot session files directory.\n' >&2
        exit 2
        ;;
esac

export DOTNET_CLI_HOME="$ARTIFACT_ROOT/dotnet-cli-home"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export TMPDIR="$ARTIFACT_ROOT/tmp"
export NUGET_PACKAGES="$ARTIFACT_ROOT/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$ARTIFACT_ROOT/nuget-http-cache"
mkdir -p "$DOTNET_CLI_HOME" "$TMPDIR" "$NUGET_PACKAGES" \
    "$NUGET_HTTP_CACHE_PATH" "$ARTIFACT_ROOT/empty-nuget-source"

if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    printf 'This spike requires macOS arm64.\n' >&2
    exit 2
fi

if ! command -v dotnet >/dev/null 2>&1 || ! command -v clang >/dev/null 2>&1; then
    printf 'Required local tools are unavailable: dotnet and clang are required.\n' >&2
    exit 2
fi
if [[ "$(dotnet --version)" != "11.0.100-rc.1.26425.128" ]]; then
    printf 'Expected .NET SDK 11.0.100-rc.1.26425.128.\n' >&2
    exit 2
fi

DOTNET_BASE_PATH="$(dotnet --info | sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)"
DOTNET_ROOT="${DOTNET_BASE_PATH%%/sdk/*}"
HOSTFXR_VERSION="11.0.0-rc.1.26425.128"
HOSTFXR="$DOTNET_ROOT/host/fxr/$HOSTFXR_VERSION/libhostfxr.dylib"
HOST_NATIVE="$DOTNET_ROOT/packs/Microsoft.NETCore.App.Host.osx-arm64/$HOSTFXR_VERSION/runtimes/osx-arm64/native"
if [[ ! -f "$HOSTFXR" || ! -f "$HOST_NATIVE/hostfxr.h" ||
      ! -f "$HOST_NATIVE/coreclr_delegates.h" ]]; then
    printf 'Matching .NET 11 RC1 hostfxr and host headers are unavailable.\n' >&2
    exit 2
fi

build_project() {
    local project="$1"
    local artifacts="$2"
    dotnet restore "$project" --source "$ARTIFACT_ROOT/empty-nuget-source" \
        --artifacts-path "$artifacts" --verbosity quiet
    dotnet build "$project" --configuration Release --no-restore \
        --artifacts-path "$artifacts" --nologo --verbosity quiet
}

HARNESS_PROJECT="$SPIKE_ROOT/ManagedHarness/NativeStreamedRoute.csproj"
ROOT_PROJECT="$REPO_ROOT/spikes/managed-streamed-closure/Fixtures/RootOriginal/RootOriginal.csproj"
build_project "$HARNESS_PROJECT" "$ARTIFACT_ROOT/harness-build"
build_project "$ROOT_PROJECT" "$ARTIFACT_ROOT/closure-build"

HARNESS_OUTPUT="$ARTIFACT_ROOT/harness-build/bin/NativeStreamedRoute/release"
CONTRACTS_DLL="$ARTIFACT_ROOT/harness-build/bin/NetLift.Contracts/release/NetLift.Contracts.dll"
NEGOTIATION_DLL="$ARTIFACT_ROOT/harness-build/bin/NetLift.Negotiation/release/NetLift.Negotiation.dll"
MANAGED_HOST_DLL="$ARTIFACT_ROOT/harness-build/bin/NetLift.ManagedHost/release/NetLift.ManagedHost.dll"
HELPER_DLL="$HARNESS_OUTPUT/NativeStreamedRoute.dll"
RUNTIME_CONFIG="$HARNESS_OUTPUT/NativeStreamedRoute.runtimeconfig.json"
ROOT_OUTPUT="$ARTIFACT_ROOT/closure-build/bin/RootOriginal/release"
STAGED_BUNDLE="$ARTIFACT_ROOT/staged-bundle"
ROOT_DLL="$STAGED_BUNDLE/ClosureRoot.dll"
DEPENDENCY_DLL="$STAGED_BUNDLE/ClosureDependency.dll"
PROVIDER_DLL="$STAGED_BUNDLE/NetLift.Negotiation.AsyncHost.Managed.New.dll"
PROBE="$ARTIFACT_ROOT/native_route"
PROBE_PID=

cleanup_probe() {
    if [[ -n "$PROBE_PID" ]] && kill -0 "$PROBE_PID" 2>/dev/null; then
        kill "$PROBE_PID"
        wait "$PROBE_PID" 2>/dev/null || true
    fi
}
trap cleanup_probe EXIT

for file in "$CONTRACTS_DLL" "$NEGOTIATION_DLL" "$MANAGED_HOST_DLL" \
    "$HELPER_DLL" "$RUNTIME_CONFIG" "$ROOT_OUTPUT/ClosureRoot.dll" \
    "$ROOT_OUTPUT/ClosureDependency.dll" \
    "$ROOT_OUTPUT/NetLift.Negotiation.AsyncHost.Managed.New.dll" \
    "$ROOT_OUTPUT/ClosureRoot.deps.json"; do
    if [[ ! -f "$file" ]]; then
        printf 'Expected isolated build output is missing: %s\n' "$file" >&2
        exit 2
    fi
done

mkdir -p "$STAGED_BUNDLE"
cp -R "$ROOT_OUTPUT/." "$STAGED_BUNDLE/"
clang -std=c11 -Wall -Wextra -Werror -pthread \
    -I "$HOST_NATIVE" "$SPIKE_ROOT/native_route.c" \
    -o "$PROBE"

HOSTFXR_HASH="$(shasum -a 256 "$HOSTFXR" | awk '{ print $1 }')"
CONTRACTS_HASH="$(shasum -a 256 "$CONTRACTS_DLL" | awk '{ print $1 }')"
NEGOTIATION_HASH="$(shasum -a 256 "$NEGOTIATION_DLL" | awk '{ print $1 }')"
MANAGED_HOST_HASH="$(shasum -a 256 "$MANAGED_HOST_DLL" | awk '{ print $1 }')"
HELPER_HASH="$(shasum -a 256 "$HELPER_DLL" | awk '{ print $1 }')"
ROOT_HASH="$(shasum -a 256 "$ROOT_DLL" | awk '{ print $1 }')"
DEPENDENCY_HASH="$(shasum -a 256 "$DEPENDENCY_DLL" | awk '{ print $1 }')"
PROVIDER_HASH="$(shasum -a 256 "$PROVIDER_DLL" | awk '{ print $1 }')"

READY="$ARTIFACT_ROOT/verified-capture.ready"
GO="$ARTIFACT_ROOT/replace-and-continue.go"
LOG="$ARTIFACT_ROOT/native-route.log"
rm -f "$READY" "$GO"
"$PROBE" \
    "$HOSTFXR" "$HOSTFXR_HASH" "$RUNTIME_CONFIG" \
    "$CONTRACTS_DLL" "$CONTRACTS_HASH" \
    "$NEGOTIATION_DLL" "$NEGOTIATION_HASH" \
    "$MANAGED_HOST_DLL" "$MANAGED_HOST_HASH" \
    "$HELPER_DLL" "$HELPER_HASH" \
    "$ROOT_DLL" "$ROOT_HASH" \
    "$DEPENDENCY_DLL" "$DEPENDENCY_HASH" \
    "$PROVIDER_DLL" "$PROVIDER_HASH" \
    "$READY" "$GO" >"$LOG" 2>&1 &
PROBE_PID=$!

READY_SEEN=false
for _ in $(seq 1 3000); do
    if [[ -e "$READY" ]]; then
        READY_SEEN=true
        break
    fi
    if ! kill -0 "$PROBE_PID" 2>/dev/null; then
        wait "$PROBE_PID" || true
        cat "$LOG"
        printf 'Native host exited before verified-capture handshake.\n' >&2
        exit 1
    fi
    sleep 0.01
done
if [[ "$READY_SEEN" != true ]]; then
    kill "$PROBE_PID"
    wait "$PROBE_PID" || true
    cat "$LOG"
    printf 'Timed out waiting for verified-capture handshake.\n' >&2
    exit 1
fi

replace_with_invalid_text() {
    local path="$1"
    local label="$2"
    local original_hash="$3"
    local replacement="$path.replacement"
    printf 'not a managed assembly: %s\n' "$label" >"$replacement"
    local replacement_hash
    replacement_hash="$(shasum -a 256 "$replacement" | awk '{ print $1 }')"
    if [[ "$replacement_hash" == "$original_hash" ]]; then
        printf 'Invalid replacement hash unexpectedly matches original: %s\n' "$path" >&2
        exit 1
    fi
    mv "$replacement" "$path"
    local observed_hash
    observed_hash="$(shasum -a 256 "$path" | awk '{ print $1 }')"
    if [[ "$observed_hash" != "$replacement_hash" ]]; then
        printf 'Atomic replacement hash mismatch at %s\n' "$path" >&2
        exit 1
    fi
    printf 'staged_replacement label=%s path=%s sha256=%s payload=invalid-text\n' \
        "$label" "$path" "$replacement_hash"
}

replace_with_invalid_text "$ROOT_DLL" root "$ROOT_HASH"
replace_with_invalid_text "$DEPENDENCY_DLL" dependency "$DEPENDENCY_HASH"
replace_with_invalid_text "$PROVIDER_DLL" generated-provider "$PROVIDER_HASH"
touch "$GO"

if ! wait "$PROBE_PID"; then
    PROBE_PID=
    cat "$LOG"
    exit 1
fi
PROBE_PID=
cat "$LOG"

grep -Fq 'NEGATIVE incorrect_expected_hash=rejected_before_load' "$LOG"
grep -Fq 'NEGATIVE kind=1 expected_failure=FileNotFoundException' "$LOG"
grep -Fq 'NEGATIVE kind=2 expected_failure=FileNotFoundException' "$LOG"
grep -Fq 'NEGATIVE kind=3 expected_failure=BadImageFormatException' "$LOG"
grep -Fq 'marker=root-original/dependency-original' "$LOG"
grep -Fq 'PATH_CONTROL register_from_invalid_path_status=1313603845 lease=0' "$LOG"
grep -Fq 'NATIVE_CALLBACK typed_confirm=accepted result_code=7 accepted=true audited=true' "$LOG"
grep -Fq 'STREAMED_ROUTE_CLOSED callback_owner_released=true' "$LOG"
grep -Fq 'PASS real ManagedHost V1 tables + generated V2 route + native callback/result + close/drain' "$LOG"

printf 'PASS streamed native route experiment; logs/artifacts: %s\n' "$ARTIFACT_ROOT"
