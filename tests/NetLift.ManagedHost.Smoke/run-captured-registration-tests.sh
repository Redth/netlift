#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
project_root="$repo_root/tests/NetLift.ManagedHost.Smoke"

if [ -z "${NETLIFT_TRUST_TMPDIR:-}" ]; then
    printf '%s\n' \
        'NETLIFT_TRUST_TMPDIR is required; use a session-owned scratch directory outside shared /tmp.' >&2
    exit 2
fi
case "$NETLIFT_TRUST_TMPDIR" in
    /*) ;;
    *)
        printf '%s\n' 'NETLIFT_TRUST_TMPDIR must be an absolute path.' >&2
        exit 2
        ;;
esac
mkdir -p "$NETLIFT_TRUST_TMPDIR"
work_root=$(CDPATH= cd -- "$NETLIFT_TRUST_TMPDIR" && pwd -P)
case "$work_root" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf '%s\n' \
            'NETLIFT_TRUST_TMPDIR must not resolve inside a shared system temp directory.' >&2
        exit 2
        ;;
esac

work=$(mktemp -d "$work_root/netlift-managed-captured-registration.XXXXXX")
work=$(CDPATH= cd -- "$work" && pwd -P)
cleanup() {
    trap - EXIT HUP INT TERM
    chmod -R u+w "$work" 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT HUP INT TERM
export TMPDIR="$work"
export NUGET_PACKAGES="$work/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$work/nuget-http-cache"
export DOTNET_CLI_HOME="$work/dotnet-home"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1
mkdir -p "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" "$DOTNET_CLI_HOME"

artifacts="$work/artifacts"
build_log="$work/build.log"
if ! dotnet build "$project_root/NetLift.ManagedHost.Smoke.csproj" \
    --configuration Debug \
    -p:NetLiftManagedHostDispatchSmoke=true \
    --artifacts-path "$artifacts" \
    --nologo \
    --verbosity quiet >"$build_log" 2>&1; then
    cat "$build_log" >&2
    exit 1
fi
grep -E 'Build succeeded\.|[0-9]+ Warning\(s\)|[0-9]+ Error\(s\)' "$build_log"

find_project_file() {
    project_name=$1
    file_name=$2
    project_bin="$artifacts/bin/$project_name"
    result=$(find "$project_bin" -type f -name "$file_name" -print -quit)
    if [ -z "$result" ]; then
        printf 'Build output not found: %s/%s\n' "$project_name" "$file_name" >&2
        exit 1
    fi
    printf '%s\n' "$result"
}

smoke=$(find_project_file NetLift.ManagedHost.Smoke NetLift.ManagedHost.Smoke.dll)
module_output=$(find_project_file NetLift.ManagedHost.CapturedModule NetLift.ManagedHost.CapturedAppModule.dll)
replacement_module=$(find_project_file NetLift.ManagedHost.CapturedModuleReplacement NetLift.ManagedHost.CapturedAppModule.dll)
replacement_private=$(find_project_file CapturedPrivateDependencyReplacement NetLift.ManagedHost.CapturedPrivateDependency.dll)
replacement_generated_provider=$(find_project_file CapturedGeneratedProviderReplacement NetLift.Negotiation.AsyncHost.Managed.New.dll)
new_generated_module=$(find_project_file NetLift.Negotiation.AsyncHost.Managed.New NetLift.Negotiation.AsyncHost.Managed.New.dll)
native_module=$(find_project_file CapturedNativeModule NetLift.ManagedHost.CapturedNativeModule.dll)

run_captured_case() {
    registration_case=$1
    case_dir="$work/cases/$registration_case"
    mkdir -p "$case_dir"
    cp -R "$(dirname -- "$module_output")/." "$case_dir/"
    dotnet "$smoke" captured-registration "$registration_case" \
        "$case_dir/NetLift.ManagedHost.CapturedAppModule.dll" \
        "$case_dir" \
        "$replacement_module" \
        "$replacement_private" \
        "$replacement_generated_provider"
}

run_captured_case positive
run_captured_case wrong-digest
run_captured_case missing-dependency
run_captured_case schema-mismatch
run_captured_case quarantine

native_case="$work/cases/native-rejected"
mkdir -p "$native_case"
cp -R "$(dirname -- "$native_module")/." "$native_case/"
dotnet "$smoke" captured-registration native-rejected \
    "$native_case/NetLift.ManagedHost.CapturedNativeModule.dll" \
    "$native_case" \
    "$replacement_module" \
    "$replacement_private" \
    "$replacement_generated_provider"

path_control="$work/path-control"
mkdir -p "$path_control"
cp -R "$(dirname -- "$new_generated_module")/." "$path_control/"
dotnet "$smoke" new api-live-route \
    "$path_control/NetLift.Negotiation.AsyncHost.Managed.New.dll" \
    "$work/path-control-scratch"

printf '%s\n' 'PASS: captured ManagedAppHost registration and existing path registration controls.'
