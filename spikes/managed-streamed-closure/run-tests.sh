#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
spike_root="$repo_root/spikes/managed-streamed-closure"

if [ -z "${NETLIFT_CLOSURE_TMPDIR:-}" ]; then
    printf '%s\n' \
        'NETLIFT_CLOSURE_TMPDIR is required; use a session-owned scratch directory outside shared /tmp.' >&2
    exit 2
fi
case "$NETLIFT_CLOSURE_TMPDIR" in
    /*) ;;
    *)
        printf '%s\n' 'NETLIFT_CLOSURE_TMPDIR must be an absolute path.' >&2
        exit 2
        ;;
esac
mkdir -p "$NETLIFT_CLOSURE_TMPDIR"
work_root=$(CDPATH= cd -- "$NETLIFT_CLOSURE_TMPDIR" && pwd -P)
case "$work_root" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf '%s\n' \
            'NETLIFT_CLOSURE_TMPDIR must not resolve inside a shared system temp directory.' >&2
        exit 2
        ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
    printf '%s\n' 'Required tool is unavailable: dotnet' >&2
    exit 2
fi

work=$(mktemp -d "$work_root/netlift-managed-streamed-closure.XXXXXX")
work=$(CDPATH= cd -- "$work" && pwd -P)
probe_pid=
cleanup() {
    if [ -n "$probe_pid" ]; then
        kill "$probe_pid" 2>/dev/null || true
        wait "$probe_pid" 2>/dev/null || true
    fi
    chmod -R u+w "$work" 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT HUP INT TERM

export TMPDIR="$work"
export NUGET_PACKAGES="$work/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$work/nuget-http-cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
mkdir -p "$work/empty-nuget-source" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"

build_project() {
    name=$1
    project=$2
    dotnet restore "$project" \
        --source "$work/empty-nuget-source" \
        -p:DirectoryBuildPropsPath="$spike_root/Directory.Build.props" \
        -p:NetLiftClosureWorkRoot="$work" \
        --verbosity quiet
    dotnet build "$project" \
        --no-restore \
        --configuration Release \
        -p:DirectoryBuildPropsPath="$spike_root/Directory.Build.props" \
        -p:NetLiftClosureWorkRoot="$work" \
        --verbosity quiet
    printf 'PASS built %s\n' "$name"
}

build_project host "$spike_root/Host/Host.csproj"
build_project dependency-replacement \
    "$spike_root/Fixtures/DependencyReplacement/DependencyReplacement.csproj"
build_project root-original "$spike_root/Fixtures/RootOriginal/RootOriginal.csproj"
build_project root-replacement "$spike_root/Fixtures/RootReplacement/RootReplacement.csproj"

host_dll="$work/bin/Host/Release/net11.0/ClosureHost.dll"
root_original_dir="$work/bin/RootOriginal/Release/net11.0"
root_replacement="$work/bin/RootReplacement/Release/net11.0/ClosureRoot.dll"
dependency_replacement="$work/bin/DependencyReplacement/Release/net11.0/ClosureDependency.dll"
for file in \
    "$host_dll" \
    "$root_original_dir/ClosureRoot.dll" \
    "$root_original_dir/ClosureRoot.deps.json" \
    "$root_original_dir/ClosureDependency.dll" \
    "$root_original_dir/NetLift.Negotiation.AsyncHost.Managed.New.dll" \
    "$root_replacement" \
    "$dependency_replacement"; do
    if [ ! -f "$file" ]; then
        printf 'Expected build output is missing: %s\n' "$file" >&2
        exit 1
    fi
done

run_probe() {
    name=$1
    mode=$2
    swap_kind=$3
    expected_marker=$4
    package="$work/bundles/$name"
    marker_dir="$work/markers/$name"
    mkdir -p "$package" "$marker_dir"
    cp -R "$root_original_dir/." "$package/"

    descriptor="$marker_dir/closure.json"
    describe_log="$marker_dir/describe.log"
    dotnet "$host_dll" describe \
        "$package" "$package/ClosureRoot.dll" "$descriptor" \
        >"$describe_log" 2>&1

    ready="$marker_dir/verified"
    continue_marker="$marker_dir/continue"
    log="$marker_dir/output.log"
    dotnet "$host_dll" "$mode" "$package/ClosureRoot.dll" \
        "$descriptor" "$ready" "$continue_marker" >"$log" 2>&1 &
    probe_pid=$!

    deadline=$(( $(date +%s) + 30 ))
    while [ ! -f "$ready" ]; do
        if ! kill -0 "$probe_pid" 2>/dev/null; then
            cat "$describe_log" "$log" >&2
            wait "$probe_pid" || true
            printf 'Probe exited before publishing its capture marker: %s\n' "$name" >&2
            exit 1
        fi
        if [ "$(date +%s)" -ge "$deadline" ]; then
            kill "$probe_pid" 2>/dev/null || true
            wait "$probe_pid" 2>/dev/null || true
            printf 'Timed out waiting for capture marker: %s\n' "$name" >&2
            exit 1
        fi
        sleep 0.01
    done

    case "$swap_kind" in
        root)
            cp "$root_replacement" "$package/ClosureRoot.replacement.dll"
            mv "$package/ClosureRoot.replacement.dll" "$package/ClosureRoot.dll"
            ;;
        dependency)
            cp "$dependency_replacement" "$package/ClosureDependency.replacement.dll"
            mv "$package/ClosureDependency.replacement.dll" "$package/ClosureDependency.dll"
            ;;
        none) ;;
        *)
            printf 'Unknown swap kind: %s\n' "$swap_kind" >&2
            exit 2
            ;;
    esac

    : > "$continue_marker"
    if ! wait "$probe_pid"; then
        probe_pid=
        cat "$describe_log" "$log" >&2
        exit 1
    fi
    probe_pid=
    cat "$describe_log" "$log"
    if ! grep -F "marker=$expected_marker" "$log" >/dev/null; then
        printf 'Unexpected root/dependency marker in probe: %s\n' "$name" >&2
        exit 1
    fi
    if ! grep -F "shared_contracts=host shared_negotiation=host api_v2=valid" "$log" >/dev/null; then
        printf 'Generated provider or shared assembly check failed: %s\n' "$name" >&2
        exit 1
    fi
}

run_probe stream-root-swap stream root root-original/dependency-original
run_probe stream-dependency-swap stream dependency root-original/dependency-original
run_probe stream-control stream none root-original/dependency-original
run_probe path-root-swap path root root-replacement/dependency-original
run_probe path-dependency-swap path dependency root-original/dependency-replacement
run_probe path-control path none root-original/dependency-original

printf '%s\n' 'PASS all managed streamed closure experiments'
