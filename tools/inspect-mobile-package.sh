#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: $0 android <app.apk> | android-aab <app.aab> | android-aar <runtime.aar> | android-generic <app.apk> [app.dll ...] | android-generic-aab <app.aab> [app.dll ...] | ios <NativeSwiftHost.app> [bridge.dll] [business.dll]" >&2
    exit 2
}

[[ $# -ge 2 ]] || usage
platform="$1"
package="$2"
shift 2
fail() {
    echo "FAIL: $*" >&2
    exit 1
}

case "$platform" in
    android-generic|android-generic-aab)
        if [[ $# == 0 ]]; then
            expected_app_dlls=(Orders.Business.dll NetLift.AndroidRuntime.OrdersContract.dll)
        else
            expected_app_dlls=("$@")
        fi
        for app_dll in "${expected_app_dlls[@]}"; do
            [[ "$app_dll" =~ ^[A-Za-z][A-Za-z0-9_.-]*\.dll$ ]] ||
                fail "app assembly must be a DLL filename without a path"
        done
        ;;
    ios) [[ $# -le 2 ]] || usage ;;
    *) [[ $# == 0 ]] || usage ;;
esac

case "$platform" in
    android-aar)
        [[ -f "$package" ]] || fail "AAR not found: $package"
        command -v unzip >/dev/null || fail "unzip is required"
        entries="$(unzip -Z1 "$package")" || fail "cannot list AAR entries"
        duplicate_entry="$(printf '%s\n' "$entries" | sort | uniq -d)"
        [[ -z "$duplicate_entry" ]] ||
            fail "duplicate AAR archive entries: $duplicate_entry"
        for required in AndroidManifest.xml classes.jar \
            jni/arm64-v8a/libcoreclr.so jni/arm64-v8a/libclrjit.so \
            jni/arm64-v8a/libnetlift_androidruntime.so; do
            grep -Fxq "$required" <<< "$entries" ||
                fail "required AAR asset missing: $required"
        done
        dlls="$(printf '%s\n' "$entries" | grep -E '\.dll$' || true)"
        [[ -n "$dlls" ]] || fail "AAR contains no managed runtime assemblies"
        unexpected="$(printf '%s\n' "$dlls" |
            grep -Ev '^assets/netlift/runtime/[A-Za-z0-9._-]+/dotnet/[^/]+\.dll$' || true)"
        [[ -z "$unexpected" ]] || fail "managed assemblies outside the versioned runtime asset root: $unexpected"
        versions="$(printf '%s\n' "$dlls" | cut -d/ -f4 | sort -u)"
        [[ "$(printf '%s\n' "$versions" | wc -l | tr -d ' ')" == 1 ]] ||
            fail "multiple managed runtime versions in AAR: $versions"
        for required in System.Private.CoreLib.dll NetLift.Contracts.dll \
            NetLift.AndroidRuntime.Managed.dll; do
            grep -Fxq "assets/netlift/runtime/$versions/dotnet/$required" <<< "$dlls" ||
                fail "required AAR asset missing: $required"
        done
        duplicate="$(printf '%s\n' "$dlls" | sed 's#^.*/##' | sort | uniq -d)"
        [[ -z "$duplicate" ]] || fail "duplicate managed assembly names: $duplicate"
        native="$(printf '%s\n' "$entries" | grep -E '\.so$' || true)"
        unexpected="$(printf '%s\n' "$native" | grep -Ev '^jni/arm64-v8a/[^/]+\.so$' || true)"
        [[ -z "$unexpected" ]] || fail "native libraries outside arm64 AAR jni root: $unexpected"
        app_specific="$(printf '%s\n' "$entries" |
            grep -Ei '(^|/)(Orders\.Business|NetLift\.Managed|NetLift\.ManagedBridge)\.dll$|(^|/)(NetLift\.kt|netlift\.h|schema\.json)$|^assets/netlift/apps/' || true)"
        [[ -z "$app_specific" ]] || fail "app-specific contract or payload in generic AAR: $app_specific"
        unexpected_runtime="$(printf '%s\n' "$dlls" | sed 's#^.*/##' |
            grep -Ev '^(System(\..+)?|Microsoft\..+|mscorlib|netstandard|WindowsBase|NetLift\.Contracts|NetLift\.AndroidRuntime\.Managed)\.dll$' || true)"
        [[ -z "$unexpected_runtime" ]] ||
            fail "non-runtime managed assemblies in generic AAR: $unexpected_runtime"
        printf 'PASS Android AAR: %s unique runtime assemblies for %s; arm64 CoreCLR/JIT present; no detected app payload or unexpected managed assembly names.\n' \
            "$(printf '%s\n' "$dlls" | wc -l | tr -d ' ')" "$versions"
        ;;
    android-generic|android-generic-aab)
        if [[ "$platform" == android-generic-aab ]]; then
            kind="AAB"
            prefix="base/"
        else
            kind="APK"
            prefix=""
        fi
        [[ -f "$package" ]] || fail "$kind not found: $package"
        command -v unzip >/dev/null || fail "unzip is required"
        entries="$(unzip -Z1 "$package")" || fail "cannot list $kind entries"
        duplicate_entry="$(printf '%s\n' "$entries" | sort | uniq -d)"
        [[ -z "$duplicate_entry" ]] ||
            fail "duplicate $kind archive entries: $duplicate_entry"
        if [[ "$platform" == android-generic-aab ]]; then
            for required in BundleConfig.pb base/manifest/AndroidManifest.xml; do
                grep -Fxq "$required" <<< "$entries" ||
                    fail "required AAB structure missing: $required"
            done
        fi
        dlls="$(printf '%s\n' "$entries" | grep -E '\.dll$' || true)"
        [[ -n "$dlls" ]] || fail "$kind contains no managed assemblies"
        unexpected="$(printf '%s\n' "$dlls" |
            grep -Ev "^${prefix}assets/netlift/runtime/[A-Za-z0-9._-]+/dotnet/[^/]+\.dll$|^${prefix}assets/netlift/apps/[A-Za-z0-9._-]+/dotnet/[^/]+\.dll$" || true)"
        [[ -z "$unexpected" ]] ||
            fail "managed assemblies outside versioned runtime or app asset roots: $unexpected"
        versions="$(printf '%s\n' "$dlls" |
            grep -E "^${prefix}assets/netlift/runtime/" | cut -d/ -f$([[ -z "$prefix" ]] && echo 4 || echo 5) | sort -u)"
        [[ "$(printf '%s\n' "$versions" | wc -l | tr -d ' ')" == 1 ]] ||
            fail "expected one managed runtime version, found: $versions"
        apps="$(printf '%s\n' "$dlls" |
            grep -E "^${prefix}assets/netlift/apps/" | cut -d/ -f$([[ -z "$prefix" ]] && echo 4 || echo 5) | sort -u)"
        [[ "$(printf '%s\n' "$apps" | wc -l | tr -d ' ')" == 1 ]] ||
            fail "expected one app-specific managed asset root, found: $apps"
        for required in System.Private.CoreLib.dll NetLift.Contracts.dll \
            NetLift.AndroidRuntime.Managed.dll; do
            grep -Fxq "${prefix}assets/netlift/runtime/$versions/dotnet/$required" <<< "$dlls" ||
                fail "required generic runtime asset missing: $required"
        done
        for required in "${expected_app_dlls[@]}"; do
            grep -Fxq "${prefix}assets/netlift/apps/$apps/dotnet/$required" <<< "$dlls" ||
                fail "required app-specific asset missing: $required"
        done
        grep -Fxq "${prefix}assets/netlift/apps/$apps/contract.json" <<< "$entries" ||
            fail "app contract manifest missing"
        command -v python3 >/dev/null || fail "python3 is required to validate the app contract manifest"
        python3 "$(dirname "$0")/validate-netlift-manifest.py" "$package" \
            "${prefix}assets/netlift/apps/$apps/contract.json" \
            "netlift/apps/$apps/dotnet" "${expected_app_dlls[@]}"
        duplicate="$(printf '%s\n' "$dlls" | sed 's#^.*/##' | sort | uniq -d)"
        [[ -z "$duplicate" ]] || fail "duplicate managed assembly names: $duplicate"
        native="$(printf '%s\n' "$entries" | grep -E '\.so$' || true)"
        unexpected="$(printf '%s\n' "$native" |
            grep -Ev "^${prefix}lib/arm64-v8a/[^/]+\.so$" || true)"
        [[ -z "$unexpected" ]] ||
            fail "native libraries outside arm64 module root: $unexpected"
        for required in libcoreclr.so libclrjit.so libnetlift_androidruntime.so; do
            grep -Fxq "${prefix}lib/arm64-v8a/$required" <<< "$native" ||
                fail "required generic native asset missing: $required"
        done
        printf 'PASS generic Android %s: runtime %s, app %s, %s managed DLLs; arm64 host/CoreCLR/JIT present.\n' \
            "$kind" "$versions" "$apps" "$(printf '%s\n' "$dlls" | wc -l | tr -d ' ')"
        ;;
    android|android-aab)
        if [[ "$platform" == android-aab ]]; then
            kind="AAB"
            prefix="base/"
        else
            kind="APK"
            prefix=""
        fi
        [[ -f "$package" ]] || fail "$kind not found: $package"
        command -v unzip >/dev/null || fail "unzip is required"
        entries="$(unzip -Z1 "$package")" || fail "cannot list $kind entries"
        if [[ "$platform" == android-aab ]]; then
            for required in BundleConfig.pb base/manifest/AndroidManifest.xml; do
                grep -Fxq "$required" <<< "$entries" ||
                    fail "required AAB structure missing: $required"
            done
        fi
        dlls="$(printf '%s\n' "$entries" | grep -E '\.dll$' || true)"
        [[ -n "$dlls" ]] || fail "$kind contains no managed assemblies"
        unexpected="$(printf '%s\n' "$dlls" | grep -Ev "^${prefix}assets/dotnet/[^/]+\.dll$" || true)"
        [[ -z "$unexpected" ]] || fail "managed assemblies outside ${prefix}assets/dotnet/: $unexpected"
        duplicate="$(printf '%s\n' "$dlls" | sed 's#^.*/##' | sort | uniq -d)"
        [[ -z "$duplicate" ]] || fail "duplicate managed assembly names: $duplicate"
        for required in \
            "${prefix}assets/dotnet/Orders.Business.dll" \
            "${prefix}assets/dotnet/NetLift.Managed.dll" \
            "${prefix}assets/dotnet/System.Private.CoreLib.dll" \
            "${prefix}lib/arm64-v8a/libcoreclr.so" \
            "${prefix}lib/arm64-v8a/libclrjit.so" \
            "${prefix}lib/arm64-v8a/libnetlift_host.so"; do
            grep -Fxq "$required" <<< "$entries" ||
                fail "required arm64 asset missing from $kind: $required"
        done
        printf 'PASS Android %s: %s unique managed assemblies under %sassets/dotnet; arm64 host/CoreCLR/JIT present.\n' \
            "$kind" "$(printf '%s\n' "$dlls" | wc -l | tr -d ' ')" "$prefix"
        ;;
    ios)
        [[ -d "$package" ]] || fail "app bundle not found: $package"
        bridge="${1:-NetLift.ManagedBridge.dll}"
        business="${2:-Orders.Business.dll}"
        [[ "$bridge" =~ ^[A-Za-z][A-Za-z0-9_.-]*\.dll$ ]] ||
            fail "bridge assembly must be a DLL filename without a path"
        [[ "$business" =~ ^[A-Za-z][A-Za-z0-9_.-]*\.dll$ ]] ||
            fail "business assembly must be a DLL filename without a path"
        for required in "$business" "$bridge" \
            System.Private.CoreLib.dll; do
            [[ -f "$package/$required" ]] ||
                fail "required asset missing from app bundle root: $required"
        done
        if [[ ! -f "$package/Frameworks/libcoreclr.framework/libcoreclr" ]] &&
            [[ ! -f "$package/libcoreclr.dylib" ]]; then
            fail "CoreCLR framework executable or root libcoreclr.dylib missing from app bundle"
        fi
        dlls="$(find "$package" -type f -name '*.dll' -print)"
        duplicate="$(printf '%s\n' "$dlls" | sed 's#^.*/##' | sort | uniq -d)"
        [[ -z "$duplicate" ]] || fail "duplicate managed assembly names: $duplicate"
        printf 'PASS iOS app: %s unique managed assemblies, %s, %s and CoreCLR present.\n' \
            "$(printf '%s\n' "$dlls" | wc -l | tr -d ' ')" "$business" "$bridge"
        ;;
    *)
        usage
        ;;
esac
