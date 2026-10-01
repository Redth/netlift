#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
spike_root="$repo_root/spikes/apple-host-trust-chain"

if [ -z "${NETLIFT_TRUST_TMPDIR:-}" ]; then
    printf '%s\n' \
        'NETLIFT_TRUST_TMPDIR is required; use a per-session scratch directory outside shared /tmp.' >&2
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

if [ -z "${NETLIFT_TRUST_HOSTFXR:-}" ]; then
    printf '%s\n' \
        'NETLIFT_TRUST_HOSTFXR is required; set it to an installed signed libhostfxr.dylib.' >&2
    exit 2
fi
case "$NETLIFT_TRUST_HOSTFXR" in
    /*) ;;
    *)
        printf '%s\n' 'NETLIFT_TRUST_HOSTFXR must be an absolute path.' >&2
        exit 2
        ;;
esac
if [ ! -f "$NETLIFT_TRUST_HOSTFXR" ]; then
    printf 'Installed hostfxr was not found: %s\n' "$NETLIFT_TRUST_HOSTFXR" >&2
    exit 2
fi

for tool in awk clang codesign dotnet ditto grep mkdir mktemp security shasum; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        printf 'Required tool is unavailable: %s\n' "$tool" >&2
        exit 2
    fi
done

identity_line=$(security find-identity -v -p codesigning 2>/dev/null |
    awk '/"Developer ID Application:/ { print; found = 1; exit } END { if (!found) exit 1 }') || {
    printf '%s\n' \
        'A valid Developer ID Application identity is required to exercise Team signing and Hardened Runtime.' >&2
    exit 2
}
signing_identity=$(printf '%s\n' "$identity_line" | awk '{ print $2 }')
if [ -z "$signing_identity" ]; then
    printf '%s\n' 'Could not resolve a Developer ID Application signing identity.' >&2
    exit 2
fi
signing_team=$(printf '%s\n' "$identity_line" |
    sed -E 's/.*\(([A-Z0-9]{10})\)".*/\1/')
if [ -z "$signing_team" ] || [ "$signing_team" = "$identity_line" ]; then
    printf '%s\n' 'Could not read the expected Team ID from the selected signing certificate.' >&2
    exit 2
fi

hostfxr_signing=$(codesign -d --verbose=4 "$NETLIFT_TRUST_HOSTFXR" 2>&1 || true)
hostfxr_team=$(printf '%s\n' "$hostfxr_signing" |
    awk -F= '/^TeamIdentifier=/ { print $2; exit }')
hostfxr_expected_team=UBF8T346G9
if [ "$hostfxr_team" != "$hostfxr_expected_team" ]; then
    printf '%s\n' \
        'The selected hostfxr is not signed by the expected Microsoft Team ID.' >&2
    exit 2
fi
if ! hostfxr_verify=$(codesign --verify --strict "$NETLIFT_TRUST_HOSTFXR" 2>&1); then
    printf '%s\n' "$hostfxr_verify" >&2
    printf '%s\n' 'Installed hostfxr signature verification failed.' >&2
    exit 2
fi

work=$(mktemp -d "$work_root/netlift-apple-host-trust-chain.XXXXXX")
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
    "$work/TrustChainProbe.app/Contents/MacOS" \
    "$work/TrustChainProbe.app/Contents/Frameworks" \
    "$work/TrustChainProbe.app/Contents/Resources"
app="$work/TrustChainProbe.app"

dotnet restore "$spike_root/managed/ManagedModule.csproj" \
    --source "$work/empty-nuget-source" \
    -p:BaseIntermediateOutputPath="$work/managed-obj/" \
    -p:BaseOutputPath="$work/managed-bin/" \
    --verbosity quiet
dotnet build "$spike_root/managed/ManagedModule.csproj" \
    --no-restore \
    --configuration Release \
    -p:BaseIntermediateOutputPath="$work/managed-obj/" \
    -p:BaseOutputPath="$work/managed-bin/" \
    --verbosity quiet

clang -std=c11 -Wall -Wextra -Werror -dynamiclib \
    -Wl,-install_name,@rpath/libfixturedep.dylib \
    "$spike_root/fixture_dependency.c" \
    -o "$work/libfixturedep.dylib"
clang -std=c11 -Wall -Wextra -Werror -dynamiclib \
    -DFIXTURE_BUILD=1 \
    -Wl,-install_name,@rpath/libfixturehostfxr.dylib \
    -L"$work" \
    -lfixturedep \
    -Wl,-rpath,@executable_path/../Frameworks \
    "$spike_root/fixture_hostfxr.c" \
    -o "$work/libfixturehostfxr-v1.dylib"
clang -std=c11 -Wall -Wextra -Werror -dynamiclib \
    -DFIXTURE_BUILD=2 \
    -Wl,-install_name,@rpath/libfixturehostfxr.dylib \
    -L"$work" \
    -lfixturedep \
    -Wl,-rpath,@executable_path/../Frameworks \
    "$spike_root/fixture_hostfxr.c" \
    -o "$work/libfixturehostfxr-v2.dylib"
clang -std=c11 -Wall -Wextra -Werror -dynamiclib \
    "$spike_root/adhoc_fixture.c" \
    -o "$work/libadhoc-fixture.dylib"
clang -std=c11 -Wall -Wextra -Werror \
    "$spike_root/trust_chain_host.c" \
    -framework Security \
    -framework CoreFoundation \
    -o "$app/Contents/MacOS/TrustChainHost"

cp "$spike_root/Info.plist" "$app/Contents/Info.plist"
cp "$work/libfixturedep.dylib" "$app/Contents/Frameworks/libfixturedep.dylib"
cp "$work/libfixturehostfxr-v1.dylib" \
    "$app/Contents/Frameworks/libfixturehostfxr.dylib"
cp "$work/libfixturehostfxr-v2.dylib" \
    "$app/Contents/Frameworks/libfixturehostfxr-v2.dylib"
cp "$work/managed-bin/Release/net11.0/ManagedModule.dll" \
    "$app/Contents/Resources/ManagedModule.dll"

module_identifier="com.netlift.apple-host-trust-chain.fixture-module"
for library in \
    "$app/Contents/Frameworks/libfixturedep.dylib" \
    "$app/Contents/Frameworks/libfixturehostfxr.dylib" \
    "$app/Contents/Frameworks/libfixturehostfxr-v2.dylib"; do
    codesign --force --sign "$signing_identity" --timestamp=none \
        --identifier "$module_identifier" \
        "$library" >/dev/null
done

module_hash=$(shasum -a 256 "$app/Contents/Frameworks/libfixturehostfxr.dylib" |
    awk '{ print $1 }')
printf 'provider=fixture.provider.apple-trust-chain\nmodule_sha256=%s\n' "$module_hash" \
    >"$app/Contents/Resources/module.manifest"
codesign --force --sign "$signing_identity" --options runtime --timestamp=none \
    "$app" >/dev/null
if ! codesign --verify --deep --strict "$app" \
    >"$work/app-signature-check.log" 2>&1; then
    cat "$work/app-signature-check.log" >&2
    exit 1
fi
printf '%s\n' 'PASS Developer ID app and same-Team nested fixture signatures verify'

app_signing=$(codesign -d --verbose=4 "$app" 2>&1)
app_team=$(printf '%s\n' "$app_signing" |
    awk -F= '/^TeamIdentifier=/ { print $2; exit }')
app_identifier=$(printf '%s\n' "$app_signing" |
    awk -F= '/^Identifier=/ { print $2; exit }')
if [ -z "$app_team" ] || [ "$app_team" = "not set" ] ||
    [ -z "$app_identifier" ] || [ "$app_team" != "$signing_team" ]; then
    printf '%s\n' 'FAIL signed host app has no Developer ID TeamIdentifier or code identifier' >&2
    exit 1
fi
if ! printf '%s\n' "$app_signing" | grep -F 'flags=' | grep -F 'runtime' >/dev/null; then
    printf '%s\n' 'FAIL host executable does not carry the Hardened Runtime flag' >&2
    printf '%s\n' "$app_signing" >&2
    exit 1
fi
app_entitlements=$(codesign -d --entitlements :- "$app" 2>&1 || true)
if printf '%s\n' "$app_entitlements" |
    grep -F 'com.apple.security.cs.disable-library-validation' >/dev/null; then
    printf '%s\n' 'FAIL host unexpectedly disables library validation' >&2
    exit 1
fi
if [ "$hostfxr_team" = "$app_team" ]; then
    printf '%s\n' \
        'The installed hostfxr and host app share a TeamIdentifier; cross-Team library validation is not covered.' >&2
    exit 2
fi
printf '%s\n' 'PASS app is Hardened Runtime signed with library validation enabled'
printf '%s\n' 'OBSERVED installed hostfxr is signed by a different TeamIdentifier'

app_requirement="identifier \"$app_identifier\" and anchor apple generic and certificate leaf[subject.OU] = \"$signing_team\""
module_requirement="identifier \"$module_identifier\" and anchor apple generic and certificate leaf[subject.OU] = \"$signing_team\""
hostfxr_requirement="anchor apple generic and certificate leaf[subject.OU] = \"$hostfxr_expected_team\""

"$app/Contents/MacOS/TrustChainHost" check "$app" "$app_requirement" \
    'Developer ID host bundle and resource seal'
wrong_team_requirement="identifier \"$app_identifier\" and anchor apple generic and certificate leaf[subject.OU] = \"0000000000\""
if "$app/Contents/MacOS/TrustChainHost" check "$app" "$wrong_team_requirement" \
    'wrong-Team host bundle' >"$work/wrong-team.log" 2>&1; then
    printf '%s\n' 'FAIL host bundle accepted the wrong external Team requirement' >&2
    exit 1
fi
grep -F 'FAIL external requirement rejects wrong-Team host bundle' \
    "$work/wrong-team.log" >/dev/null
printf '%s\n' 'PASS external wrong-Team requirement is rejected'

adhoc_identifier="com.netlift.apple-host-trust-chain.adhoc-fixture"
codesign --force --sign - --timestamp=none --identifier "$adhoc_identifier" \
    "$work/libadhoc-fixture.dylib" >/dev/null
adhoc_signing=$(codesign -d --verbose=4 "$work/libadhoc-fixture.dylib" 2>&1)
adhoc_hash=$(printf '%s\n' "$adhoc_signing" |
    awk -F= '/^CDHash=/ { print $2; exit }')
if [ -z "$adhoc_hash" ]; then
    printf '%s\n' 'FAIL could not read ad-hoc CodeDirectory hash' >&2
    exit 1
fi
adhoc_requirement="identifier \"$adhoc_identifier\" and cdhash H\"$adhoc_hash\""
"$app/Contents/MacOS/TrustChainHost" check "$work/libadhoc-fixture.dylib" \
    "$adhoc_requirement" 'ad-hoc CodeDirectory hash fixture'
wrong_hash_requirement="identifier \"$adhoc_identifier\" and cdhash H\"0000000000000000000000000000000000000000\""
if "$app/Contents/MacOS/TrustChainHost" check "$work/libadhoc-fixture.dylib" \
    "$wrong_hash_requirement" 'wrong ad-hoc CodeDirectory hash' \
    >"$work/wrong-hash.log" 2>&1; then
    printf '%s\n' 'FAIL ad-hoc fixture accepted an incorrect external cdhash requirement' >&2
    exit 1
fi
grep -F 'FAIL external requirement rejects wrong ad-hoc CodeDirectory hash' \
    "$work/wrong-hash.log" >/dev/null
printf '%s\n' 'PASS incorrect ad-hoc CodeDirectory hash requirement is rejected'

tampered_app="$work/TamperedResource.app"
ditto "$app" "$tampered_app"
printf 'tampered-resource\n' >>"$tampered_app/Contents/Resources/ManagedModule.dll"
if "$app/Contents/MacOS/TrustChainHost" check "$tampered_app" "$app_requirement" \
    'tampered managed resource bundle' >"$work/tampered-resource.log" 2>&1; then
    printf '%s\n' 'FAIL app validation accepted managed resource tampering' >&2
    exit 1
fi
grep -F 'FAIL external requirement rejects tampered managed resource bundle' \
    "$work/tampered-resource.log" >/dev/null
printf '%s\n' 'PASS external Team requirement check detects managed resource tampering'

hostfxr_result="$work/hostfxr-load.log"
if ! "$app/Contents/MacOS/TrustChainHost" load-hostfxr \
    "$app" "$NETLIFT_TRUST_HOSTFXR" "$app_requirement" "$hostfxr_requirement" \
    >"$hostfxr_result" 2>&1; then
    cat "$hostfxr_result" >&2
    exit 1
fi
cat "$hostfxr_result"
if ! grep -E '^RESULT hardened library validation (denied|allowed) cross-Team hostfxr' \
    "$hostfxr_result" >/dev/null; then
    printf '%s\n' 'FAIL real hostfxr loader outcome was not classified' >&2
    exit 1
fi

run_race() {
    race_app=$1
    loader=$2
    expected_build=$3
    label=$4
    markers="$work/$label-markers"
    race_log="$work/$label-race.log"
    original="$work/$label-original.dylib"
    if [ "$loader" = "fd" ]; then
        expected_route=/dev/fd
    else
        expected_route=path
    fi
    mkdir "$markers"

    "$race_app/Contents/MacOS/TrustChainHost" run-fixture \
        "$race_app" "$loader" "$expected_build" "$app_requirement" \
        "$module_requirement" "$markers" >"$race_log" 2>&1 &
    host_pid=$!
    attempt=0
    while [ ! -f "$markers/verified" ] && [ "$attempt" -lt 100 ]; do
        if ! kill -0 "$host_pid" 2>/dev/null; then
            cat "$race_log" >&2
            printf 'FAIL %s host exited before trust checks completed\n' "$label" >&2
            exit 1
        fi
        attempt=$((attempt + 1))
        sleep 0.1
    done
    if [ ! -f "$markers/verified" ]; then
        printf 'FAIL timed out waiting for %s trust-check marker\n' "$label" >&2
        exit 1
    fi

    mv "$race_app/Contents/Frameworks/libfixturehostfxr.dylib" "$original"
    cp "$race_app/Contents/Frameworks/libfixturehostfxr-v2.dylib" \
        "$race_app/Contents/Frameworks/libfixturehostfxr.dylib"
    touch "$markers/release"

    if wait "$host_pid"; then
        host_status=0
    else
        host_status=$?
    fi
    host_pid=
    cat "$race_log"
    if [ "$host_status" -ne 0 ] ||
        ! grep -F "PASS loaded provider=fixture.provider.apple-trust-chain build=$expected_build dependency=73 via=$expected_route" \
            "$race_log" >/dev/null; then
        printf 'FAIL %s did not load the expected module image (status %s)\n' \
            "$label" "$host_status" >&2
        exit 1
    fi

    if "$app/Contents/MacOS/TrustChainHost" check "$race_app" "$app_requirement" \
        'post-swap app bundle' >"$work/$label-post-swap-check.log" 2>&1; then
        printf 'OBSERVED %s post-swap host-bundle validation accepted same-Team/same-identifier replacement\n' \
            "$label"
    else
        printf 'OBSERVED %s post-swap host-bundle validation rejected replacement\n' "$label"
        cat "$work/$label-post-swap-check.log"
    fi
}

path_race_app="$work/PathRace.app"
fd_race_app="$work/FdRace.app"
ditto "$app" "$path_race_app"
ditto "$app" "$fd_race_app"
run_race "$path_race_app" path 2 path
run_race "$fd_race_app" fd 1 fd
printf '%s\n' 'Apple host trust-chain experiment completed'
