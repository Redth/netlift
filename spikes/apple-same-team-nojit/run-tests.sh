#!/bin/bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
spike_root="$repo_root/spikes/apple-same-team-nojit"

if [[ -z "${NETLIFT_TRUST_TMPDIR:-}" ]]; then
    printf '%s\n' \
        'NETLIFT_TRUST_TMPDIR is required; use this session files directory, not /tmp.' >&2
    exit 2
fi
if [[ "$NETLIFT_TRUST_TMPDIR" != /* ]]; then
    printf '%s\n' 'NETLIFT_TRUST_TMPDIR must be an absolute path.' >&2
    exit 2
fi
mkdir -p "$NETLIFT_TRUST_TMPDIR"
work_root=$(cd -- "$NETLIFT_TRUST_TMPDIR" && pwd -P)
case "$work_root" in
    /tmp|/tmp/*|/private/tmp|/private/tmp/*|/var/tmp|/var/tmp/*|/private/var/tmp|/private/var/tmp/*)
        printf '%s\n' 'NETLIFT_TRUST_TMPDIR must not resolve inside shared system temp.' >&2
        exit 2
        ;;
esac

if [[ -z "${NETLIFT_DOTNET_ROOT:-}" || "$NETLIFT_DOTNET_ROOT" != /* ]]; then
    printf '%s\n' 'NETLIFT_DOTNET_ROOT must be set to an absolute installed .NET root.' >&2
    exit 2
fi
dotnet_root=$(cd -- "$NETLIFT_DOTNET_ROOT" && pwd -P)
sdk_version=11.0.100-rc.1.26425.128
runtime_version=11.0.0-rc.1.26425.128
hostfxr_source="$dotnet_root/host/fxr/$runtime_version"
runtime_source="$dotnet_root/shared/Microsoft.NETCore.App/$runtime_version"
host_pack="$dotnet_root/packs/Microsoft.NETCore.App.Host.osx-arm64/$runtime_version/runtimes/osx-arm64/native"
nuget_feed="$HOME/.nuget/packages"
crossgen2_package="$nuget_feed/microsoft.netcore.app.crossgen2.osx-arm64/$runtime_version"
runtime_package="$nuget_feed/microsoft.netcore.app.runtime.osx-arm64/$runtime_version"

for required_path in \
    "$dotnet_root/dotnet" \
    "$hostfxr_source/libhostfxr.dylib" \
    "$runtime_source/libhostpolicy.dylib" \
    "$runtime_source/libcoreclr.dylib" \
    "$host_pack/hostfxr.h" \
    "$host_pack/coreclr_delegates.h" \
    "$host_pack/host_error_codes.h" \
    "$host_pack/nethost.h" \
    "$host_pack/libnethost.a" \
    "$crossgen2_package/microsoft.netcore.app.crossgen2.osx-arm64.$runtime_version.nupkg" \
    "$crossgen2_package/tools/crossgen2" \
    "$runtime_package/microsoft.netcore.app.runtime.osx-arm64.$runtime_version.nupkg"; do
    if [[ ! -f "$required_path" ]]; then
        printf 'Required offline .NET RC1 asset is missing: %s\n' "$required_path" >&2
        exit 2
    fi
done
if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
    printf '%s\n' 'This bounded runner requires macOS arm64.' >&2
    exit 2
fi

for tool in awk bash clang codesign ditto dotnet file find grep mkdir mktemp otool python3 security shasum; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        printf 'Required tool is unavailable: %s\n' "$tool" >&2
        exit 2
    fi
done

identity_line=$(security find-identity -v -p codesigning 2>/dev/null |
    awk '/"Developer ID Application:/ { print; found = 1; exit } END { if (!found) exit 1 }') || {
    printf '%s\n' \
        'A valid Developer ID Application identity is required for same-Team signing.' >&2
    exit 2
}
signing_identity=$(printf '%s\n' "$identity_line" | awk '{ print $2 }')
signing_team=$(printf '%s\n' "$identity_line" |
    sed -E 's/.*\(([A-Z0-9]{10})\)".*/\1/')
if [[ -z "$signing_identity" || -z "$signing_team" || "$signing_team" == "$identity_line" ]]; then
    printf '%s\n' 'Could not resolve the installed Developer ID identity and Team ID.' >&2
    exit 2
fi

microsoft_team=UBF8T346G9
original_hostfxr="$hostfxr_source/libhostfxr.dylib"
original_hostfxr_signing=$(codesign -d --verbose=4 "$original_hostfxr" 2>&1)
original_hostfxr_team=$(printf '%s\n' "$original_hostfxr_signing" |
    awk -F= '/^TeamIdentifier=/ { print $2; exit }')
if [[ "$original_hostfxr_team" != "$microsoft_team" ||
      "$original_hostfxr_team" == "$signing_team" ]] ||
    ! codesign --verify --strict "$original_hostfxr" >/dev/null 2>&1; then
    printf '%s\n' \
        'The original hostfxr must be validly signed by Microsoft and differ from the host Team.' >&2
    exit 2
fi
source_native_count=0
while IFS= read -r -d '' candidate; do
    [[ "$(file -b "$candidate")" == Mach-O* ]] || continue
    source_team=$(codesign -d --verbose=4 "$candidate" 2>&1 |
        awk -F= '/^TeamIdentifier=/ { print $2; exit }')
    if [[ "$source_team" != "$microsoft_team" ]] ||
        ! codesign --verify --strict "$candidate" >/dev/null 2>&1; then
        printf 'Installed runtime source is not valid Microsoft-signed code: %s\n' \
            "$candidate" >&2
        exit 2
    fi
    source_native_count=$((source_native_count + 1))
done < <(find "$hostfxr_source" "$runtime_source" -type f -print0)
dotnet_host_team=$(codesign -d --verbose=4 "$dotnet_root/dotnet" 2>&1 |
    awk -F= '/^TeamIdentifier=/ { print $2; exit }')
if [[ "$source_native_count" -lt 14 ||
      "$dotnet_host_team" != "$microsoft_team" ]] ||
    ! codesign --verify --strict "$dotnet_root/dotnet" >/dev/null 2>&1; then
    printf '%s\n' 'Installed Microsoft runtime native closure precheck failed.' >&2
    exit 2
fi
printf 'PASS Microsoft signatures verified on %s runtime Mach-O assets and dotnet host\n' \
    "$source_native_count"

work=$(mktemp -d "$work_root/netlift-apple-same-team-nojit.XXXXXX")
work=$(cd -- "$work" && pwd -P)
cleanup() {
    chmod -R u+w "$work" 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT HUP INT TERM

export TMPDIR="$work"
export DOTNET_CLI_HOME="$work/dotnet-cli-home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_MULTILEVEL_LOOKUP=0
export NUGET_PACKAGES="$work/nuget-packages"
unset DYLD_LIBRARY_PATH DYLD_FRAMEWORK_PATH DYLD_FALLBACK_LIBRARY_PATH
unset DYLD_FALLBACK_FRAMEWORK_PATH DYLD_INSERT_LIBRARIES

if [[ "$("$dotnet_root/dotnet" --version)" != "$sdk_version" ]]; then
    printf 'Expected .NET SDK %s under NETLIFT_DOTNET_ROOT.\n' "$sdk_version" >&2
    exit 2
fi
if [[ "$(file -b "$crossgen2_package/tools/crossgen2")" != *"Mach-O 64-bit executable arm64"* ]]; then
    printf '%s\n' 'Cached Crossgen2 compiler is not a native arm64 Mach-O.' >&2
    exit 2
fi
printf 'PASS offline .NET SDK %s and arm64 Crossgen2 prechecks\n' "$sdk_version"

mkdir -p \
    "$work/managed-obj" \
    "$work/managed-bin" \
    "$work/NoJitHost.app/Contents/MacOS" \
    "$work/NoJitHost.app/Contents/Resources/component-r2r" \
    "$work/NoJitHost.app/Contents/Resources/component-il" \
    "$work/NoJitHost.app/Contents/Resources/dotnetroot"
app="$work/NoJitHost.app"
runtime_root="$app/Contents/Resources/dotnetroot"

if ! "$dotnet_root/dotnet" restore "$spike_root/managed/ManagedProbe.csproj" \
    --runtime osx-arm64 \
    --source "$nuget_feed" \
    -p:PublishReadyToRun=true \
    -p:RestorePackagesPath="$NUGET_PACKAGES" \
    -p:BaseIntermediateOutputPath="$work/managed-obj/" \
    --verbosity quiet >"$work/restore.log" 2>&1; then
    cat "$work/restore.log" >&2
    printf '%s\n' 'FAIL offline restore from the local NuGet package cache.' >&2
    exit 1
fi

publish_component() {
    local mode=$1
    local publish_r2r=$2
    local publish_dir="$work/component-$mode-publish"
    if ! "$dotnet_root/dotnet" publish "$spike_root/managed/ManagedProbe.csproj" \
        --no-restore \
        --configuration Release \
        --runtime osx-arm64 \
        --self-contained false \
        -p:UseAppHost=false \
        -p:PublishReadyToRun="$publish_r2r" \
        -p:RestorePackagesPath="$NUGET_PACKAGES" \
        -p:BaseIntermediateOutputPath="$work/managed-obj/" \
        -p:BaseOutputPath="$work/managed-bin/$mode/" \
        -p:PublishDir="$publish_dir/" \
        --verbosity quiet >"$work/publish-$mode.log" 2>&1; then
        cat "$work/publish-$mode.log" >&2
        printf 'FAIL framework-dependent %s publish.\n' "$mode" >&2
        return 1
    fi
    for output in ManagedProbe.dll ManagedProbe.runtimeconfig.json ManagedProbe.deps.json; do
        if [[ ! -f "$publish_dir/$output" ]]; then
            printf 'FAIL %s publish is missing %s.\n' "$mode" "$output" >&2
            return 1
        fi
    done
}

publish_component r2r true
publish_component il false
python3 "$spike_root/check-r2r.py" \
    "$work/component-r2r-publish/ManagedProbe.dll" r2r
python3 "$spike_root/check-r2r.py" \
    "$work/component-il-publish/ManagedProbe.dll" il
python3 "$spike_root/check-r2r.py" \
    "$runtime_source/System.Private.CoreLib.dll" r2r
for runtime_config in \
    "$work/component-r2r-publish/ManagedProbe.runtimeconfig.json" \
    "$work/component-il-publish/ManagedProbe.runtimeconfig.json"; do
    grep -F '"System.Runtime.TieredCompilation": false' "$runtime_config" >/dev/null
    grep -F '"System.Runtime.TieredCompilation.QuickJit": false' "$runtime_config" >/dev/null
done
printf '%s\n' 'PASS tiered compilation and Quick JIT are disabled in runtime configuration'
printf 'PASS R2R component SHA-256: %s\n' \
    "$(shasum -a 256 "$work/component-r2r-publish/ManagedProbe.dll" | awk '{ print $1 }')"

ditto "$work/component-r2r-publish" "$app/Contents/Resources/component-r2r"
ditto "$work/component-il-publish" "$app/Contents/Resources/component-il"
mkdir -p \
    "$runtime_root/host/fxr" \
    "$runtime_root/shared/Microsoft.NETCore.App"
ditto "$hostfxr_source" "$runtime_root/host/fxr/$runtime_version"
ditto "$runtime_source" "$runtime_root/shared/Microsoft.NETCore.App/$runtime_version"
cp "$dotnet_root/dotnet" "$runtime_root/dotnet"
if find "$runtime_root" -type l -print | grep .; then
    printf '%s\n' 'FAIL staged runtime closure contains symlinks.' >&2
    exit 1
fi
jit_library="$runtime_root/shared/Microsoft.NETCore.App/$runtime_version/libclrjit.dylib"
if [[ ! -f "$jit_library" ]]; then
    printf '%s\n' 'FAIL expected source JIT library is missing before runtime staging.' >&2
    exit 1
fi
rm -f "$jit_library"
if find "$runtime_root" -name 'libclrjit*' -print | grep .; then
    printf '%s\n' 'FAIL JIT library remains in staged runtime root.' >&2
    exit 1
fi
printf '%s\n' 'PASS staged framework-dependent runtime closure has no JIT library'

clang -std=c11 -Wall -Wextra -Werror \
    -I"$host_pack" \
    "$spike_root/native_host.c" \
    "$host_pack/libnethost.a" \
    -framework Security \
    -framework CoreFoundation \
    -o "$app/Contents/MacOS/RuntimeHost"

native_count=0
while IFS= read -r -d '' candidate; do
    description=$(file -b "$candidate")
    case "$description" in
        Mach-O*)
            codesign --force --sign "$signing_identity" --timestamp=none \
                "$candidate" >/dev/null 2>&1
            signed_team=$(codesign -d --verbose=4 "$candidate" 2>&1 |
                awk -F= '/^TeamIdentifier=/ { print $2; exit }')
            if [[ "$signed_team" != "$signing_team" ]] ||
                ! codesign --verify --strict "$candidate" >/dev/null 2>&1; then
                printf 'FAIL staged native asset signature: %s\n' \
                    "${candidate#"$runtime_root"/}" >&2
                exit 1
            fi
            if otool -L "$candidate" | grep -F "$dotnet_root" >/dev/null; then
                printf 'FAIL staged native asset references the installed runtime path: %s\n' \
                    "${candidate#"$runtime_root"/}" >&2
                exit 1
            fi
            native_count=$((native_count + 1))
            ;;
    esac
done < <(find "$runtime_root" -type f -print0)
if [[ "$native_count" -lt 10 ]]; then
    printf 'FAIL incomplete signed runtime closure: %s Mach-O files\n' "$native_count" >&2
    exit 1
fi
printf 'PASS same-Team identity/signature and dependency checks for %s staged Mach-O files\n' \
    "$native_count"

cp "$spike_root/Info.plist" "$app/Contents/Info.plist"
codesign --force --sign "$signing_identity" --options runtime --timestamp=none \
    "$app" >/dev/null 2>&1
if ! codesign --verify --deep --strict "$app" >"$work/app-verify.log" 2>&1; then
    cat "$work/app-verify.log" >&2
    exit 1
fi
app_signing=$(codesign -d --verbose=4 "$app" 2>&1)
app_team=$(printf '%s\n' "$app_signing" |
    awk -F= '/^TeamIdentifier=/ { print $2; exit }')
if [[ "$app_team" != "$signing_team" ]] ||
    ! printf '%s\n' "$app_signing" | grep -F 'flags=' | grep -F 'runtime' >/dev/null; then
    printf '%s\n' 'FAIL scratch host lacks its expected Team or Hardened Runtime flag.' >&2
    exit 1
fi
entitlements=$(codesign -d --entitlements :- "$app" 2>&1 || true)
if printf '%s\n' "$entitlements" |
    grep -E 'com\.apple\.security\.cs\.(allow-jit|disable-library-validation|allow-unsigned-executable-memory)' >/dev/null; then
    printf '%s\n' 'FAIL scratch host contains a prohibited JIT/code-signing entitlement.' >&2
    exit 1
fi
printf '%s\n' 'PASS Hardened Runtime enabled; JIT, unsigned-code, and library-validation override entitlements absent'

NETLIFT_SAME_TEAM_ID="$signing_team" \
    "$app/Contents/MacOS/RuntimeHost" foreign-hostfxr \
    "$original_hostfxr" "$microsoft_team"

altered_hostfxr="$work/altered-libhostfxr.dylib"
cp "$runtime_root/host/fxr/$runtime_version/libhostfxr.dylib" "$altered_hostfxr"
printf 'altered\n' >>"$altered_hostfxr"
if codesign --verify --strict "$altered_hostfxr" >/dev/null 2>&1; then
    printf '%s\n' 'FAIL altered staged hostfxr retained a valid code signature.' >&2
    exit 1
fi
printf '%s\n' 'PASS altered staged runtime binary fails code signature verification'

run_host() {
    local mode=$1
    local log="$work/host-$mode.log"
    local component_dir="$app/Contents/Resources/component-$mode"
    if NETLIFT_SAME_TEAM_ID="$signing_team" \
        DOTNET_ROOT="$runtime_root" \
        DOTNET_ROOT_ARM64="$runtime_root" \
        DOTNET_ReadyToRun=1 \
        DOTNET_TieredCompilation=0 \
        DOTNET_TC_QuickJit=0 \
        DOTNET_TC_QuickJitForLoops=0 \
        COREHOST_TRACE=1 \
        COREHOST_TRACE_VERBOSITY=4 \
        COREHOST_TRACEFILE="$work/hostfxr-$mode-trace.log" \
        "$app/Contents/MacOS/RuntimeHost" run \
        "$runtime_root" \
        "$component_dir/ManagedProbe.runtimeconfig.json" \
        "$component_dir/ManagedProbe.dll" >"$log" 2>&1; then
        cat "$log"
        for expected in \
            'PASS hostfxr resolves Microsoft.NETCore.App under staged root' \
            'PASS hostfxr_initialize_for_runtime_config' \
            'PASS managed API invoked result=202611' \
            'PASS no libclrjit image is loaded in the host process'; do
            if ! grep -F "$expected" "$log" >/dev/null; then
                printf 'FAIL successful host process omitted required evidence: %s\n' \
                    "$expected" >&2
                return 1
            fi
        done
        return 0
    fi
    cat "$log" >&2
    return 1
}

is_expected_nojit_failure() {
    local log=$1
    grep -F 'Failed to create CoreCLR, HRESULT: 0x80070008' "$log" >/dev/null &&
        grep -F 'FAIL hostfxr_get_runtime_delegate status=0x80008089' "$log" >/dev/null
}

if ! run_host r2r; then
    if is_expected_nojit_failure "$work/host-r2r.log"; then
        if run_host il >"$work/host-il-control-captured.log" 2>&1; then
            printf '%s\n' 'FAIL IL-only control unexpectedly ran without libclrjit.' >&2
            cat "$work/host-il-control-captured.log" >&2
            exit 1
        fi
        if ! is_expected_nojit_failure "$work/host-il-control-captured.log"; then
            printf '%s\n' 'FAIL IL-only control failed for an unclassified reason.' >&2
            cat "$work/host-il-control-captured.log" >&2
            exit 1
        fi
        printf '%s\n' \
            'PASS IL-only control independently failed at CoreCLR creation without libclrjit'
        printf '%s\n' \
            'RESULT=NO_JIT_UNSUPPORTED: R2R component and CoreLib were verified, but CoreCLR creation failed without libclrjit (0x80070008; hostfxr delegate 0x80008089).'
        exit 0
    fi
    printf '%s\n' 'FAIL R2R no-JIT attempt failed for an unclassified reason.' >&2
    exit 1
fi

if run_host il >"$work/host-il-control-captured.log" 2>&1; then
    printf '%s\n' 'FAIL IL-only control unexpectedly ran without libclrjit.' >&2
    cat "$work/host-il-control-captured.log" >&2
    exit 1
fi
cat "$work/host-il-control-captured.log"
if ! is_expected_nojit_failure "$work/host-il-control-captured.log"; then
    printf '%s\n' 'FAIL IL-only control did not fail with the expected missing-JIT evidence.' >&2
    exit 1
fi
printf '%s\n' 'PASS IL-only control failed closed without the JIT library'
printf '%s\n' 'RESULT=R2R_MANAGED_INVOCATION_WITHOUT_JIT'
