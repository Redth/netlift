#!/bin/bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
spike_root="$repo_root/spikes/apple-same-team-runtime"

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
for required_path in \
    "$dotnet_root/dotnet" \
    "$hostfxr_source/libhostfxr.dylib" \
    "$runtime_source/libhostpolicy.dylib" \
    "$runtime_source/libcoreclr.dylib" \
    "$host_pack/hostfxr.h" \
    "$host_pack/coreclr_delegates.h" \
    "$host_pack/host_error_codes.h" \
    "$host_pack/nethost.h" \
    "$host_pack/libnethost.a"; do
    if [[ ! -f "$required_path" ]]; then
        printf 'Required .NET 11 RC1 asset is missing: %s\n' "$required_path" >&2
        exit 2
    fi
done
if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
    printf '%s\n' 'This bounded runner requires macOS arm64.' >&2
    exit 2
fi

for tool in awk bash clang codesign ditto dotnet file find grep mkdir mktemp otool security shasum; do
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
      "$original_hostfxr_team" == "$signing_team" ]]; then
    printf '%s\n' \
        'The original .NET hostfxr must be validly signed by Microsoft and differ from the host Team.' >&2
    exit 2
fi
if ! codesign --verify --strict "$original_hostfxr" >/dev/null 2>&1; then
    printf '%s\n' 'Original Microsoft hostfxr signature verification failed.' >&2
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

work=$(mktemp -d "$work_root/netlift-apple-same-team-runtime.XXXXXX")
work=$(cd -- "$work" && pwd -P)
host_pid=
cleanup() {
    if [[ -n "$host_pid" ]]; then
        kill "$host_pid" 2>/dev/null || true
        wait "$host_pid" 2>/dev/null || true
    fi
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
export NUGET_PACKAGES="$work/nuget-packages"
if [[ "$("$dotnet_root/dotnet" --version)" != "$sdk_version" ]]; then
    printf 'Expected .NET SDK %s under NETLIFT_DOTNET_ROOT.\n' "$sdk_version" >&2
    exit 2
fi
printf 'PASS Microsoft signatures verified on %s source runtime Mach-O files and dotnet host\n' \
    "$source_native_count"

mkdir -p \
    "$work/empty-nuget-source" \
    "$work/managed-obj" \
    "$work/managed-bin" \
    "$work/TrustHost.app/Contents/MacOS" \
    "$work/TrustHost.app/Contents/Resources/component" \
    "$work/TrustHost.app/Contents/Resources/dotnetroot"
app="$work/TrustHost.app"
runtime_root="$app/Contents/Resources/dotnetroot"

if ! "$dotnet_root/dotnet" restore "$spike_root/managed/ManagedProbe.csproj" \
    --source "$work/empty-nuget-source" \
    -p:SelfContained=false \
    -p:RestorePackagesPath="$NUGET_PACKAGES" \
    -p:BaseIntermediateOutputPath="$work/managed-obj/" \
    --verbosity quiet >"$work/restore.log" 2>&1; then
    cat "$work/restore.log" >&2
    printf '%s\n' 'FAIL offline restore of framework-dependent probe.' >&2
    exit 1
fi
if ! "$dotnet_root/dotnet" publish "$spike_root/managed/ManagedProbe.csproj" \
    --no-restore \
    --configuration Release \
    --self-contained false \
    -p:UseAppHost=false \
    -p:RestorePackagesPath="$NUGET_PACKAGES" \
    -p:BaseIntermediateOutputPath="$work/managed-obj/" \
    -p:BaseOutputPath="$work/managed-bin/" \
    -p:PublishDir="$work/component-publish/" \
    --verbosity quiet >"$work/publish.log" 2>&1; then
    cat "$work/publish.log" >&2
    printf '%s\n' 'FAIL framework-dependent publish of probe component.' >&2
    exit 1
fi
if [[ ! -f "$work/component-publish/ManagedProbe.dll" ||
      ! -f "$work/component-publish/ManagedProbe.runtimeconfig.json" ||
      ! -f "$work/component-publish/ManagedProbe.deps.json" ]]; then
    printf '%s\n' 'FAIL framework-dependent publish outputs are incomplete.' >&2
    exit 1
fi
grep -F '"name": "Microsoft.NETCore.App"' \
    "$work/component-publish/ManagedProbe.runtimeconfig.json" >/dev/null
grep -F "\"version\": \"$runtime_version\"" \
    "$work/component-publish/ManagedProbe.runtimeconfig.json" >/dev/null
ditto "$work/component-publish" "$app/Contents/Resources/component"
printf '%s\n' 'PASS published .NET component is framework-dependent and runtime-configured'

mkdir -p \
    "$runtime_root/host/fxr" \
    "$runtime_root/shared/Microsoft.NETCore.App"
ditto "$hostfxr_source" "$runtime_root/host/fxr/$runtime_version"
ditto "$runtime_source" "$runtime_root/shared/Microsoft.NETCore.App/$runtime_version"
cp "$dotnet_root/dotnet" "$runtime_root/dotnet"
if find "$runtime_root" -type l -print | grep .; then
    printf '%s\n' 'FAIL staged .NET runtime closure contains symlinks.' >&2
    exit 1
fi
printf '%s\n' 'PASS staged .NET runtime closure contains no symlinks'
printf '%s\n' 'PASS staged hostfxr, hostpolicy, CoreCLR, and complete runtime directory in scratch app'

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
            if [[ "$signed_team" != "$signing_team" ]]; then
                printf 'FAIL staged native asset has unexpected Team ID: %s\n' \
                    "${candidate#"$runtime_root"/}" >&2
                exit 1
            fi
            if ! codesign --verify --strict "$candidate" >/dev/null 2>&1; then
                printf 'FAIL staged native signature verification: %s\n' \
                    "${candidate#"$runtime_root"/}" >&2
                exit 1
            fi
            if otool -L "$candidate" | grep -F "$dotnet_root" >/dev/null; then
                printf 'FAIL staged native closure retains original installation path: %s\n' \
                    "${candidate#"$runtime_root"/}" >&2
                exit 1
            fi
            native_count=$((native_count + 1))
            ;;
    esac
done < <(find "$runtime_root" -type f -print0)
if [[ "$native_count" -lt 10 ]]; then
    printf 'FAIL incomplete native runtime closure: signed %s Mach-O files\n' "$native_count" >&2
    exit 1
fi
printf 'PASS same-Team signatures and dependency path checks for %s staged Mach-O files\n' \
    "$native_count"

cp "$spike_root/Info.plist" "$app/Contents/Info.plist"
cat >"$work/jit-only.entitlements" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.security.cs.allow-jit</key>
    <true/>
</dict>
</plist>
EOF
codesign --force --sign "$signing_identity" --options runtime --timestamp=none \
    --entitlements "$work/jit-only.entitlements" \
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
    printf '%s\n' 'FAIL host app signature lacks expected Team ID or Hardened Runtime.' >&2
    exit 1
fi
entitlements=$(codesign -d --entitlements :- "$app" 2>&1 || true)
if ! printf '%s\n' "$entitlements" |
    grep -F 'com.apple.security.cs.allow-jit' >/dev/null; then
    printf '%s\n' 'FAIL host app lacks the CoreCLR JIT entitlement.' >&2
    exit 1
fi
if printf '%s\n' "$entitlements" |
    grep -F 'com.apple.security.cs.disable-library-validation' >/dev/null; then
    printf '%s\n' 'FAIL host app disables library validation.' >&2
    exit 1
fi
printf '%s\n' 'PASS Developer ID host has Hardened Runtime and library validation enabled'

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
printf '%s\n' 'PASS altered runtime binary fails code signature verification'

runtime_config="$app/Contents/Resources/component/ManagedProbe.runtimeconfig.json"
managed_assembly="$app/Contents/Resources/component/ManagedProbe.dll"
if ! NETLIFT_SAME_TEAM_ID="$signing_team" \
    DOTNET_ROOT="$runtime_root" \
    DOTNET_ROOT_ARM64="$runtime_root" \
    COREHOST_TRACE=1 \
    COREHOST_TRACE_VERBOSITY=4 \
    COREHOST_TRACEFILE="$work/hostfxr-trace.log" \
    "$app/Contents/MacOS/RuntimeHost" run \
    "$runtime_root" "$runtime_config" "$managed_assembly" \
    >"$work/runtime-host.log" 2>&1; then
    cat "$work/runtime-host.log" >&2
    if [[ -f "$work/hostfxr-trace.log" ]]; then
        tail -n 100 "$work/hostfxr-trace.log" >&2
    fi
    printf '%s\n' 'FAIL same-Team staged framework-dependent runtime invocation.' >&2
    exit 1
fi
cat "$work/runtime-host.log"
grep -F 'PASS nethost resolves hostfxr inside staged DOTNET_ROOT' \
    "$work/runtime-host.log" >/dev/null
grep -F 'PASS hostfxr resolves Microsoft.NETCore.App under staged root' \
    "$work/runtime-host.log" >/dev/null
grep -F 'PASS hostfxr_initialize_for_runtime_config' \
    "$work/runtime-host.log" >/dev/null
grep -F 'PASS managed API invoked result=202611' \
    "$work/runtime-host.log" >/dev/null

printf '%s\n' 'Apple same-Team framework-dependent runtime experiment completed'
