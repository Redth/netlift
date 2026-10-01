#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
spike_root="$repo_root/spikes/managed-assembly-load-identity"

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

for tool in awk clang cmp dotnet grep nm otool python3 shasum sort tail uname; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        printf 'Required tool is unavailable: %s\n' "$tool" >&2
        exit 2
    fi
done

if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
    printf '%s\n' \
        'The native dependency fixture currently requires macOS arm64 and its Apple linker.' >&2
    exit 2
fi

work=$(mktemp -d "$work_root/netlift-managed-assembly-identity.XXXXXX")
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
mkdir -p \
    "$work/empty-nuget-source" "$work/local-feed" "$work/bin" "$work/obj" \
    "$work/packages" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"

native_original="$work/libfixture_native-original.dylib"
native_replacement="$work/libfixture_native-replacement.dylib"
native_package_asset="$work/native-package-source/libfixture_native.dylib"
mkdir -p "$(dirname -- "$native_package_asset")"
clang -std=c11 -Wall -Wextra -Werror -dynamiclib -arch arm64 \
    -Wl,-install_name,@rpath/libfixture_native.dylib \
    "$spike_root/Fixtures/native-dependency-original.c" \
    -o "$native_original"
clang -std=c11 -Wall -Wextra -Werror -dynamiclib -arch arm64 \
    -Wl,-install_name,@rpath/libfixture_native.dylib \
    "$spike_root/Fixtures/native-dependency-replacement.c" \
    -o "$native_replacement"
cp "$native_original" "$native_package_asset"

native_package="$spike_root/Fixtures/NativeAssetPackage/NativeAssetPackage.csproj"
dotnet restore "$native_package" \
    --source "$work/empty-nuget-source" \
    -p:BaseIntermediateOutputPath="$work/obj/native-package/" \
    -p:BaseOutputPath="$work/bin/native-package/" \
    --verbosity quiet
dotnet pack "$native_package" \
    --no-restore \
    --configuration Release \
    --output "$work/local-feed" \
    -p:FixtureNativeLibrary="$native_package_asset" \
    -p:BaseIntermediateOutputPath="$work/obj/native-package/" \
    -p:BaseOutputPath="$work/bin/native-package/" \
    --verbosity quiet

build_project() {
    name=$1
    project=$2
    dotnet restore "$project" \
        --source "$work/local-feed" \
        --source "$work/empty-nuget-source" \
        -p:BaseIntermediateOutputPath="$work/obj/$name/" \
        -p:BaseOutputPath="$work/bin/$name/" \
        --verbosity quiet
    dotnet build "$project" \
        --no-restore \
        --configuration Release \
        -p:BaseIntermediateOutputPath="$work/obj/$name/" \
        -p:BaseOutputPath="$work/bin/$name/" \
        --verbosity quiet
}

build_project dependency-original \
    "$spike_root/Fixtures/DependencyOriginal/DependencyOriginal.csproj"
build_project dependency-replacement \
    "$spike_root/Fixtures/DependencyReplacement/DependencyReplacement.csproj"
build_project root-original "$spike_root/Fixtures/RootOriginal/RootOriginal.csproj"
build_project root-replacement "$spike_root/Fixtures/RootReplacement/RootReplacement.csproj"
build_project host "$spike_root/Host/Host.csproj"

host_dll="$work/bin/host/Release/net11.0/Host.dll"
root_original_dir="$work/bin/root-original/Release/net11.0"
root_replacement_dir="$work/bin/root-replacement/Release/net11.0"
root_original="$root_original_dir/RootFixture.dll"
root_replacement="$root_replacement_dir/RootFixture.dll"
dependency_original="$work/bin/dependency-original/Release/net11.0/FixtureDependency.dll"
dependency_replacement="$work/bin/dependency-replacement/Release/net11.0/FixtureDependency.dll"
root_deps="$root_original_dir/RootFixture.deps.json"
native_relative="runtimes/osx-arm64/native/libfixture_native.dylib"
native_asset="$root_original_dir/$native_relative"

for file in "$host_dll" "$root_original" "$root_replacement" \
    "$dependency_original" "$dependency_replacement" "$root_deps"; do
    if [ ! -f "$file" ]; then
        printf 'Expected build output is missing: %s\n' "$file" >&2
        exit 1
    fi
done
python3 "$spike_root/verify-native-deps.py" "$root_deps" "$native_relative"
mkdir -p "$(dirname -- "$native_asset")"
cp "$native_package_asset" "$native_asset"
if cmp -s "$root_original" "$root_replacement"; then
    printf '%s\n' 'Root fixtures unexpectedly have identical bytes.' >&2
    exit 1
fi

original_exports="$work/native-original-exports.txt"
replacement_exports="$work/native-replacement-exports.txt"
nm -gU "$native_original" | awk '{ print $NF }' | sort > "$original_exports"
nm -gU "$native_replacement" | awk '{ print $NF }' | sort > "$replacement_exports"
if ! cmp -s "$original_exports" "$replacement_exports" ||
    ! grep -F '_fixture_native_marker' "$original_exports" >/dev/null; then
    printf '%s\n' 'Native fixture exports differ or omit the required marker ABI.' >&2
    exit 1
fi
native_original_hash=$(shasum -a 256 "$native_original" | awk '{ print $1 }')
native_replacement_hash=$(shasum -a 256 "$native_replacement" | awk '{ print $1 }')
if [ "$native_original_hash" = "$native_replacement_hash" ]; then
    printf '%s\n' 'Native original and replacement unexpectedly have identical hashes.' >&2
    exit 1
fi
native_original_id=$(otool -D "$native_original" | tail -n 1)
native_replacement_id=$(otool -D "$native_replacement" | tail -n 1)
if [ "$native_original_id" != "$native_replacement_id" ]; then
    printf '%s\n' 'Native fixture install names differ.' >&2
    exit 1
fi
printf 'NATIVE_FIXTURE original_sha256=%s replacement_sha256=%s install_name=%s export=_fixture_native_marker\n' \
    "$native_original_hash" "$native_replacement_hash" "$native_original_id"

run_probe() {
    name=$1
    mode=$2
    swap_kind=$3
    expected_marker=$4
    package="$work/packages/$name"
    marker_dir="$work/markers/$name"
    mkdir -p "$package" "$marker_dir"
    cp -R "$root_original_dir/." "$package/"

    ready="$marker_dir/verified"
    continue_marker="$marker_dir/continue"
    descriptor="$marker_dir/root.descriptor"
    log="$marker_dir/output.log"
    expected_hash=$(shasum -a 256 "$package/RootFixture.dll" | awk '{ print $1 }')
    printf '%s\n%s\n%s\n' \
        "$package/RootFixture.dll" \
        'RootFixture, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null' \
        "$expected_hash" > "$descriptor"
    dotnet "$host_dll" "$mode" "$package/RootFixture.dll" \
        "$descriptor" "$ready" "$continue_marker" \
        >"$log" 2>&1 &
    probe_pid=$!

    deadline=$(( $(date +%s) + 30 ))
    while [ ! -f "$ready" ]; do
        if ! kill -0 "$probe_pid" 2>/dev/null; then
            cat "$log" >&2
            wait "$probe_pid" || true
            printf 'Probe exited before publishing its verification marker: %s\n' "$name" >&2
            exit 1
        fi
        if [ "$(date +%s)" -ge "$deadline" ]; then
            kill "$probe_pid" 2>/dev/null || true
            wait "$probe_pid" 2>/dev/null || true
            printf 'Timed out waiting for verification marker: %s\n' "$name" >&2
            exit 1
        fi
        sleep 0.01
    done

    case "$swap_kind" in
        root)
            cp "$root_replacement" "$package/RootFixture.replacement.dll"
            mv "$package/RootFixture.replacement.dll" "$package/RootFixture.dll"
            ;;
        dependency)
            cp "$dependency_replacement" "$package/FixtureDependency.replacement.dll"
            mv "$package/FixtureDependency.replacement.dll" "$package/FixtureDependency.dll"
            ;;
        native)
            cp "$native_replacement" "$package/$native_relative.replacement"
            mv "$package/$native_relative.replacement" "$package/$native_relative"
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
        cat "$log" >&2
        exit 1
    fi
    probe_pid=
    cat "$log"
    if ! grep -F "marker=$expected_marker" "$log" >/dev/null; then
        printf 'Unexpected observed marker in probe: %s\n' "$name" >&2
        exit 1
    fi
    if ! grep -F \
        "NATIVE_RESOLVE name=fixture_native path=$package/$native_relative" \
        "$log" >/dev/null; then
        printf 'AssemblyDependencyResolver did not resolve the native fixture: %s\n' "$name" >&2
        exit 1
    fi
}

run_probe path-root-swap path root root-replacement/dependency-original/native-731
run_probe stream-root-swap stream root root-original/dependency-original/native-731
run_probe stream-dependency-swap stream dependency root-original/dependency-replacement/native-731
run_probe native-baseline path none root-original/dependency-original/native-731
run_probe stream-native-swap stream native root-original/dependency-original/native-947

printf '%s\n' 'PASS all managed and native assembly load identity experiments'
