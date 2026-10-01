#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../../.." && pwd)
spike_root="$repo_root/spikes/module-asset-trust"
apple_root="$spike_root/apple"

if [ -z "${NETLIFT_TRUST_TMPDIR:-}" ]; then
    printf '%s\n' \
        'NETLIFT_TRUST_TMPDIR is required; set it to a per-session scratch directory outside shared /tmp.' >&2
    exit 2
fi
case "$NETLIFT_TRUST_TMPDIR" in
    /*) ;;
    *)
        printf '%s\n' 'NETLIFT_TRUST_TMPDIR must be an absolute path.' >&2
        exit 2
        ;;
esac
if ! mkdir -p "$NETLIFT_TRUST_TMPDIR"; then
    printf 'Could not create NETLIFT_TRUST_TMPDIR: %s\n' "$NETLIFT_TRUST_TMPDIR" >&2
    exit 2
fi
work_root=$(CDPATH= cd -- "$NETLIFT_TRUST_TMPDIR" && pwd -P) || {
    printf 'Could not resolve NETLIFT_TRUST_TMPDIR: %s\n' "$NETLIFT_TRUST_TMPDIR" >&2
    exit 2
}
case "$work_root" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf '%s\n' \
            'NETLIFT_TRUST_TMPDIR must not resolve inside a shared system temp directory.' >&2
        exit 2
        ;;
esac

for tool in clang codesign ditto dotnet shasum; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        printf 'Required tool is unavailable: %s\n' "$tool" >&2
        exit 2
    fi
done

work=$(mktemp -d "$work_root/netlift-module-asset-trust-apple.XXXXXX")
work=$(CDPATH= cd -- "$work" && pwd -P)
host_pid=
cleanup() {
    if [ -n "$host_pid" ]; then
        kill "$host_pid" 2>/dev/null || true
        wait "$host_pid" 2>/dev/null || true
    fi
    chmod -R u+w "$work" 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT HUP INT TERM
export TMPDIR="$work"

mkdir -p \
    "$work/empty-nuget-source" \
    "$work/managed-obj" \
    "$work/managed-bin" \
    "$work/TrustProbe.app/Contents/MacOS" \
    "$work/TrustProbe.app/Contents/Frameworks" \
    "$work/TrustProbe.app/Contents/Resources"
app="$work/TrustProbe.app"

dotnet restore "$apple_root/managed/ManagedModule.csproj" \
    --source "$work/empty-nuget-source" \
    -p:BaseIntermediateOutputPath="$work/managed-obj/" \
    -p:BaseOutputPath="$work/managed-bin/" \
    --verbosity quiet
dotnet build "$apple_root/managed/ManagedModule.csproj" \
    --no-restore \
    --configuration Release \
    -p:BaseIntermediateOutputPath="$work/managed-obj/" \
    -p:BaseOutputPath="$work/managed-bin/" \
    --verbosity quiet

clang -std=c11 -Wall -Wextra -Werror -dynamiclib \
    -Wl,-install_name,@rpath/libmoduledep.dylib \
    "$apple_root/fixture_dependency.c" \
    -o "$work/libmoduledep.dylib"
clang -std=c11 -Wall -Wextra -Werror -dynamiclib \
    -DFIXTURE_BUILD=1 \
    -Wl,-install_name,@rpath/libmodule.dylib \
    "$apple_root/fixture_module.c" \
    -L"$work" \
    -lmoduledep \
    -Wl,-rpath,@executable_path/../Frameworks \
    -o "$work/module-v1.dylib"
clang -std=c11 -Wall -Wextra -Werror -dynamiclib \
    -DFIXTURE_BUILD=2 \
    -Wl,-install_name,@rpath/libmodule.dylib \
    "$apple_root/fixture_module.c" \
    -L"$work" \
    -lmoduledep \
    -Wl,-rpath,@executable_path/../Frameworks \
    -o "$work/module-v2.dylib"
clang -std=c11 -Wall -Wextra -Werror \
    "$apple_root/trust_host.c" \
    "$spike_root/module_asset_trust.c" \
    -Wl,-rpath,@executable_path/../Frameworks \
    -framework Security \
    -framework CoreFoundation \
    -o "$app/Contents/MacOS/TrustHost"

cp "$apple_root/Info.plist" "$app/Contents/Info.plist"
cp "$work/module-v1.dylib" "$app/Contents/Frameworks/libmodule.dylib"
cp "$work/module-v2.dylib" "$app/Contents/Frameworks/libmodule-v2.dylib"
cp "$work/libmoduledep.dylib" "$app/Contents/Frameworks/libmoduledep.dylib"
cp "$work/managed-bin/Release/net11.0/ManagedModule.dll" \
    "$app/Contents/Resources/ManagedModule.dll"

codesign --force --sign - --timestamp=none \
    "$app/Contents/Frameworks/libmodule.dylib"
codesign --force --sign - --timestamp=none \
    "$app/Contents/Frameworks/libmodule-v2.dylib"
codesign --force --sign - --timestamp=none \
    "$app/Contents/Frameworks/libmoduledep.dylib"
module_hash=$(shasum -a 256 "$app/Contents/Frameworks/libmodule.dylib" | awk '{ print $1 }')
printf 'provider=fixture.provider.v1\nmodule_sha256=%s\n' "$module_hash" \
    > "$app/Contents/Resources/module.manifest"
codesign --force --sign - --timestamp=none "$app"

if ! codesign --verify --deep --strict "$app" >"$work/initial-verify.log" 2>&1; then
    cat "$work/initial-verify.log" >&2
    exit 1
fi
printf '%s\n' 'PASS codesign verifies ad hoc app bundle and sealed resources'

codesign -dv --verbose=4 "$app" >"$work/signing-details.txt" 2>&1
if ! grep -F 'Signature=adhoc' "$work/signing-details.txt" >/dev/null; then
    cat "$work/signing-details.txt" >&2
    printf '%s\n' 'Expected an ad hoc signature.' >&2
    exit 1
fi
if ! grep -F 'TeamIdentifier=not set' "$work/signing-details.txt" >/dev/null; then
    cat "$work/signing-details.txt" >&2
    printf '%s\n' 'Expected no signing team identity for ad hoc code.' >&2
    exit 1
fi
printf '%s\n' 'PASS signature is ad hoc and has no TeamIdentifier'
codesign -d -r- "$app" >"$work/designated-requirement.txt" 2>&1 || true
grep 'designated =>' "$work/designated-requirement.txt" || true

chmod -R a-w "$app"
if ! codesign --verify --deep --strict "$app" >"$work/readonly-verify.log" 2>&1; then
    cat "$work/readonly-verify.log" >&2
    exit 1
fi
if ! "$app/Contents/MacOS/TrustHost" "$app" normal >"$work/normal-run.log" 2>&1; then
    cat "$work/normal-run.log" >&2
    exit 1
fi
cat "$work/normal-run.log"
grep -F 'PASS loaded provider=fixture.provider.v1 module build=1 dependency=73' \
    "$work/normal-run.log" >/dev/null

content_tampered="$work/ContentTampered.app"
ditto "$app" "$content_tampered"
chmod u+w "$content_tampered/Contents/Resources"
chmod u+w "$content_tampered/Contents/Resources/ManagedModule.dll"
printf '\ncontent-tamper\n' >> "$content_tampered/Contents/Resources/ManagedModule.dll"
if codesign --verify --deep --strict "$content_tampered" \
    >"$work/content-tamper-verify.log" 2>&1; then
    printf '%s\n' 'FAIL codesign accepted managed resource content tampering' >&2
    exit 1
fi
printf '%s\n' 'PASS codesign detects managed assembly resource content tampering'

symlink_tampered="$work/SymlinkTampered.app"
ditto "$app" "$symlink_tampered"
chmod u+w "$symlink_tampered/Contents/Frameworks"
rm "$symlink_tampered/Contents/Frameworks/libmodule.dylib"
ln -s libmodule-v2.dylib "$symlink_tampered/Contents/Frameworks/libmodule.dylib"
if codesign --verify --deep --strict "$symlink_tampered" \
    >"$work/symlink-tamper-verify.log" 2>&1; then
    printf '%s\n' 'OBSERVED codesign accepted sealed module path replaced by symlink'
    if "$symlink_tampered/Contents/MacOS/TrustHost" "$symlink_tampered" normal \
        >"$work/symlink-host.log" 2>&1; then
        cat "$work/symlink-host.log"
        printf '%s\n' 'OBSERVED host loaded module through accepted symlink'
    else
        cat "$work/symlink-host.log"
        grep -F 'FAIL local module binding: path rejected' "$work/symlink-host.log" \
            >/dev/null
        printf '%s\n' 'PASS no-follow module binder rejects accepted symlink path'
    fi
else
    printf '%s\n' 'PASS codesign detects module path replaced by a symlink'
fi

writable_bundle="$work/WritableBundle.app"
ditto "$app" "$writable_bundle"
chmod -R u+w "$writable_bundle"
if codesign --verify --deep --strict "$writable_bundle" \
    >"$work/writable-verify.log" 2>&1; then
    if [ ! -w "$writable_bundle/Contents/Frameworks" ] ||
        [ ! -w "$writable_bundle/Contents/Frameworks/libmodule.dylib" ]; then
        printf '%s\n' 'FAIL writable bundle fixture did not become owner-writable' >&2
        exit 1
    fi
    printf '%s\n' 'PASS unchanged signature verifies while bundle and module path are writable'
    if "$writable_bundle/Contents/MacOS/TrustHost" "$writable_bundle" normal \
        >"$work/writable-host.log" 2>&1; then
        printf '%s\n' 'FAIL app-owned module binder accepted writable bundle path' >&2
        exit 1
    fi
    grep -F 'FAIL local module binding: writable asset or directory' \
        "$work/writable-host.log" >/dev/null
    printf '%s\n' 'PASS app-owned module binder rejects writable bundle path'
else
    printf '%s\n' 'PASS codesign verification detects writable mode change'
fi

run_swap_test() {
    race_app=$1
    mode=$2
    expected_build=$3
    label=$4
    marker_directory="$work/$label-markers"
    race_log="$work/$label-run.log"
    original_module="$work/$label-original.dylib"
    mkdir "$marker_directory"

    "$race_app/Contents/MacOS/TrustHost" "$race_app" "$mode" "$marker_directory" \
        >"$race_log" 2>&1 &
    host_pid=$!
    attempt=0
    while [ ! -f "$marker_directory/verified" ] && [ "$attempt" -lt 100 ]; do
        if ! kill -0 "$host_pid" 2>/dev/null; then
            cat "$race_log" >&2
            printf 'FAIL %s host exited before the post-verification pause\n' "$label" >&2
            exit 1
        fi
        attempt=$((attempt + 1))
        sleep 0.1
    done
    if [ ! -f "$marker_directory/verified" ]; then
        printf 'FAIL timed out waiting for %s host verification marker\n' "$label" >&2
        exit 1
    fi

    chmod u+w "$race_app/Contents/Frameworks"
    mv "$race_app/Contents/Frameworks/libmodule.dylib" "$original_module"
    cp "$race_app/Contents/Frameworks/libmodule-v2.dylib" \
        "$race_app/Contents/Frameworks/libmodule.dylib"
    touch "$marker_directory/release"

    if wait "$host_pid"; then
        host_status=0
    else
        host_status=$?
    fi
    host_pid=
    cat "$race_log"
    grep -F 'PASS retained module fd still identifies the originally verified inode' \
        "$race_log" >/dev/null
    if [ "$mode" = "race-fd" ] &&
        ! grep -F 'PASS SecStaticCodeCheckValidity on pinned /dev/fd module' \
            "$race_log" >/dev/null; then
        printf '%s\n' 'FAIL race-fd did not validate the pinned module signature through /dev/fd' >&2
        exit 1
    fi
    if ! codesign --verify --deep --strict "$race_app" \
        >"$work/$label-post-race-verify.log" 2>&1; then
        printf 'PASS subsequent codesign verification detects the %s-test module replacement\n' \
            "$label"
    else
        printf 'FAIL codesign accepted %s path replacement after validation\n' "$label" >&2
        exit 1
    fi

    if [ "$host_status" -eq 0 ] &&
        grep -F "PASS loaded provider=fixture.provider.v1 module build=$expected_build dependency=73" \
            "$race_log" >/dev/null; then
        printf 'RESULT %s loader resolved expected module build %s and dependency 73\n' \
            "$label" "$expected_build"
        return 0
    fi
    if [ "$mode" = "race-fd" ]; then
        if grep -F 'FAIL dlopen(/dev/fd/' "$race_log" >/dev/null; then
            printf '%s\n' 'FAIL /dev/fd loader could not load the pinned module' >&2
        elif grep -E 'FAIL loaded provider=.*module build=' "$race_log" >/dev/null; then
            printf '%s\n' \
                'FAIL /dev/fd loader returned an unexpected provider, module build, or dependency' >&2
        else
            printf 'FAIL /dev/fd race exited with status %s without the expected original module build %s\n' \
                "$host_status" "$expected_build" >&2
        fi
        exit 1
    fi
    printf 'FAIL %s race did not produce a classified loader result\n' "$label" >&2
    exit 1
}

path_race_app="$work/PathRace.app"
fd_race_app="$work/FdRace.app"
ditto "$app" "$path_race_app"
ditto "$app" "$fd_race_app"
run_swap_test "$path_race_app" race-path 2 path
run_swap_test "$fd_race_app" race-fd 1 fd
printf '%s\n' 'Apple path and /dev/fd loader experiments completed'
