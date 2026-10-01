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

work=$(mktemp -d "$work_root/netlift-managed-captured-loader.XXXXXX")
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
replacement_dependency=$(find_project_file CapturedPrivateDependencyReplacement NetLift.ManagedHost.CapturedPrivateDependency.dll)
native_module=$(find_project_file CapturedNativeModule NetLift.ManagedHost.CapturedNativeModule.dll)

run_module_case() {
    mode=$1
    case_dir="$work/cases/$mode"
    mkdir -p "$case_dir"
    cp -R "$(dirname -- "$module_output")/." "$case_dir/"
    root_path="$case_dir/NetLift.ManagedHost.CapturedAppModule.dll"
    dotnet "$smoke" captured "$mode" "$root_path" "$case_dir" \
        "$replacement_module" "$replacement_dependency"
}

run_module_case captured-baseline
run_module_case captured-root-swap
run_module_case captured-dependency-swap
run_module_case path-root-control
run_module_case path-dependency-control
run_module_case wrong-root-bytes
run_module_case wrong-dependency-bytes
run_module_case mismatched-identity
run_module_case missing-dependency
run_module_case shared-copy-mismatch
run_module_case duplicate-selector
run_module_case duplicate-identity
run_module_case path-substitution
run_module_case one-shot
run_module_case quarantine
run_module_case native-map-rejected
run_module_case runtime-targets-native-rejected
run_module_case runtime-targets-managed-accepted
run_module_case unselected-target-native-ignored

native_dir="$work/cases/native-rejected"
mkdir -p "$native_dir"
cp -R "$(dirname -- "$native_module")/." "$native_dir/"
dotnet "$smoke" captured native-rejected \
    "$native_dir/NetLift.ManagedHost.CapturedNativeModule.dll" \
    "$native_dir" "$replacement_module" "$replacement_dependency"

printf '%s\n' 'PASS: all captured managed loader smoke cases.'
