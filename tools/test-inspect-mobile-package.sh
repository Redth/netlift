#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
scratch="$(mktemp -d "$root/.netlift-package-test.XXXXXX")"
trap 'rm -rf -- "$scratch"' EXIT

expect_rejection() {
    local expected="$1"
    shift
    local output
    if output="$("$@" 2>&1)"; then
        echo "FAIL: invalid package passed the audit" >&2
        exit 1
    fi
    if [[ "$output" != *"$expected"* ]]; then
        printf 'FAIL: unexpected package-audit error: %s\n' "$output" >&2
        exit 1
    fi
}

mkdir -p "$scratch/android/assets/dotnet" "$scratch/android/lib/arm64-v8a"
touch "$scratch/android/assets/dotnet/"{Orders.Business,NetLift.Managed,System.Private.CoreLib}.dll
touch "$scratch/android/lib/arm64-v8a/"{libcoreclr,libclrjit,libnetlift_host}.so
(
    cd "$scratch/android"
    zip -qr "$scratch/valid.apk" assets lib
)
bash "$root/tools/inspect-mobile-package.sh" android "$scratch/valid.apk"

aar="$scratch/runtime-aar"
version="11.0.0-rc.1"
mkdir -p "$aar/assets/netlift/runtime/$version/dotnet" "$aar/jni/arm64-v8a"
touch "$aar/AndroidManifest.xml" "$aar/classes.jar" \
    "$aar/assets/netlift/runtime/$version/dotnet/"{System.Private.CoreLib,NetLift.Contracts,NetLift.AndroidRuntime.Managed}.dll \
    "$aar/jni/arm64-v8a/"{libcoreclr,libclrjit,libnetlift_androidruntime}.so
(
    cd "$aar"
    zip -qr "$scratch/valid.aar" AndroidManifest.xml classes.jar assets jni
)
bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/valid.aar"

if command -v python3 >/dev/null; then
    python3 - "$scratch/valid.aar" "$scratch/duplicate-entry.aar" <<'PY'
import sys
import warnings
import zipfile

with zipfile.ZipFile(sys.argv[1]) as source, zipfile.ZipFile(sys.argv[2], "w") as target:
    for entry in source.infolist():
        target.writestr(entry, source.read(entry))
    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", message="Duplicate name: .*")
        target.writestr("jni/arm64-v8a/libcoreclr.so", b"duplicate")
PY
    expect_rejection "duplicate AAR archive entries" \
        bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/duplicate-entry.aar"
fi

touch "$aar/assets/netlift/runtime/$version/dotnet/Orders.Business.dll"
(
    cd "$aar"
    zip -qr "$scratch/app-specific.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "app-specific contract or payload" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/app-specific.aar"
rm "$aar/assets/netlift/runtime/$version/dotnet/Orders.Business.dll"

touch "$aar/assets/netlift/runtime/$version/dotnet/Different.Contract.dll"
(
    cd "$aar"
    zip -qr "$scratch/other-contract.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "non-runtime managed assemblies in generic AAR" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/other-contract.aar"
rm "$aar/assets/netlift/runtime/$version/dotnet/Different.Contract.dll"

mkdir -p "$aar/assets/netlift/apps/sample" "$aar/assets/dotnet"
touch "$aar/assets/netlift/apps/sample/schema.json" "$aar/assets/dotnet/Unexpected.dll"
(
    cd "$aar"
    zip -qr "$scratch/mislocated.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "outside the versioned runtime asset root" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/mislocated.aar"
rm "$aar/assets/dotnet/Unexpected.dll"
(
    cd "$aar"
    zip -qr "$scratch/app-schema.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "app-specific contract or payload" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/app-schema.aar"
rm "$aar/assets/netlift/apps/sample/schema.json"

mkdir -p "$aar/assets/netlift/runtime/11.0.1/dotnet"
touch "$aar/assets/netlift/runtime/11.0.1/dotnet/Extra.dll"
(
    cd "$aar"
    zip -qr "$scratch/mixed-runtime.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "multiple managed runtime versions" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/mixed-runtime.aar"
rm "$aar/assets/netlift/runtime/11.0.1/dotnet/Extra.dll"

mkdir -p "$aar/jni/x86_64"
touch "$aar/jni/x86_64/libcoreclr.so"
(
    cd "$aar"
    zip -qr "$scratch/mislocated-native.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "native libraries outside arm64 AAR jni root" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/mislocated-native.aar"
rm "$aar/jni/x86_64/libcoreclr.so"

rm "$aar/jni/arm64-v8a/libclrjit.so"
(
    cd "$aar"
    zip -qr "$scratch/missing-aar-jit.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "jni/arm64-v8a/libclrjit.so" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/missing-aar-jit.aar"

touch "$aar/jni/arm64-v8a/libclrjit.so"
rm "$aar/assets/netlift/runtime/$version/dotnet/NetLift.AndroidRuntime.Managed.dll"
(
    cd "$aar"
    zip -qr "$scratch/missing-aar-host.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "NetLift.AndroidRuntime.Managed.dll" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/missing-aar-host.aar"

touch "$aar/assets/netlift/runtime/$version/dotnet/NetLift.AndroidRuntime.Managed.dll"
rm "$aar/jni/arm64-v8a/libnetlift_androidruntime.so"
(
    cd "$aar"
    zip -qr "$scratch/missing-aar-native-host.aar" AndroidManifest.xml classes.jar assets jni
)
expect_rejection "jni/arm64-v8a/libnetlift_androidruntime.so" \
    bash "$root/tools/inspect-mobile-package.sh" android-aar "$scratch/missing-aar-native-host.aar"

generic="$scratch/generic"
app="com.netlift.sample"
mkdir -p "$generic/assets/netlift/runtime/$version/dotnet" \
    "$generic/assets/netlift/apps/$app/dotnet" "$generic/lib/arm64-v8a"
touch "$generic/assets/netlift/runtime/$version/dotnet/"{System.Private.CoreLib,NetLift.Contracts,NetLift.AndroidRuntime.Managed}.dll \
    "$generic/assets/netlift/apps/$app/dotnet/"{Orders.Business,NetLift.AndroidRuntime.OrdersContract}.dll \
    "$generic/lib/arm64-v8a/"{libcoreclr,libclrjit,libnetlift_androidruntime}.so
cat > "$generic/assets/netlift/apps/$app/contract.json" <<EOF
{"assemblyName":"NetLift.AndroidRuntime.OrdersContract","abiVersion":1,"requiredFeatureBits":15,"schemaHash":"$(printf 'a%.0s' {1..64})","managedAssetDirectory":"netlift/apps/$app/dotnet"}
EOF
(
    cd "$generic"
    zip -qr "$scratch/valid-generic.apk" assets lib
)
bash "$root/tools/inspect-mobile-package.sh" android-generic "$scratch/valid-generic.apk"

other="$scratch/other-generic"
cp -R "$generic" "$other"
rm "$other/assets/netlift/apps/$app/dotnet/"{Orders.Business,NetLift.AndroidRuntime.OrdersContract}.dll
touch "$other/assets/netlift/apps/$app/dotnet/"{Different.Business,Different.Contract}.dll
cat > "$other/assets/netlift/apps/$app/contract.json" <<EOF
{"assemblyName":"Different.Contract","abiVersion":1,"requiredFeatureBits":15,"schemaHash":"$(printf 'b%.0s' {1..64})","managedAssetDirectory":"netlift/apps/$app/dotnet"}
EOF
(
    cd "$other"
    zip -qr "$scratch/other-generic.apk" assets lib
)
bash "$root/tools/inspect-mobile-package.sh" android-generic "$scratch/other-generic.apk" \
    Different.Business.dll Different.Contract.dll
expect_rejection "Orders.Business.dll" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic "$scratch/other-generic.apk"
expect_rejection "Different.Contract.dll" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic "$scratch/valid-generic.apk" \
    Different.Contract.dll
expect_rejection "app assembly must be a DLL filename" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic "$scratch/other-generic.apk" \
    ../Different.Contract.dll

generic_bundle="$scratch/generic-bundle"
mkdir -p "$generic_bundle/base"
cp -R "$generic/assets" "$generic/lib" "$generic_bundle/base/"
mkdir -p "$generic_bundle/base/manifest"
touch "$generic_bundle/BundleConfig.pb" "$generic_bundle/base/manifest/AndroidManifest.xml"
(
    cd "$generic_bundle"
    zip -qr "$scratch/valid-generic.aab" BundleConfig.pb base
)
bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/valid-generic.aab"
other_bundle="$scratch/other-generic-bundle"
mkdir -p "$other_bundle/base/manifest"
cp "$generic_bundle/BundleConfig.pb" "$other_bundle/"
cp "$generic_bundle/base/manifest/AndroidManifest.xml" "$other_bundle/base/manifest/"
cp -R "$other/assets" "$other/lib" "$other_bundle/base/"
(
    cd "$other_bundle"
    zip -qr "$scratch/other-generic.aab" BundleConfig.pb base
)
bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/other-generic.aab" \
    Different.Business.dll Different.Contract.dll
expect_rejection "Different.Contract.dll" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/valid-generic.aab" \
    Different.Contract.dll
printf '%s\n' '{"assemblyName":"Different.Contract","assemblyName":"Different.Contract"}' \
    > "$other_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$other_bundle"
    zip -qr "$scratch/duplicate-contract-field.aab" BundleConfig.pb base
)
expect_rejection "duplicate contract manifest field" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/duplicate-contract-field.aab" \
    Different.Contract.dll
printf '%s\n' '{"assemblyName":"Different.Contract","abiVersion":1,"requiredFeatureBits":15,"schemaHash":"bad","managedAssetDirectory":"netlift/apps/com.netlift.sample/dotnet"}' \
    > "$other_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$other_bundle"
    zip -qr "$scratch/bad-contract-hash.aab" BundleConfig.pb base
)
expect_rejection "schemaHash must be 64 lowercase hex characters" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/bad-contract-hash.aab" \
    Different.Contract.dll
printf '%s\n' '{"assemblyName":"Orders.Business","abiVersion":1,"requiredFeatureBits":15,"schemaHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","managedAssetDirectory":"netlift/apps/com.netlift.sample/dotnet"}' \
    > "$other_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$other_bundle"
    zip -qr "$scratch/bad-contract-assembly.aab" BundleConfig.pb base
)
expect_rejection "assemblyName does not match expected app assemblies" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/bad-contract-assembly.aab" \
    Different.Contract.dll
printf '%s\n' '{"assemblyName":"Different.Contract","abiVersion":1,"requiredFeatureBits":15,"schemaHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","managedAssetDirectory":"netlift/apps/wrong/dotnet"}' \
    > "$other_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$other_bundle"
    zip -qr "$scratch/bad-contract-root.aab" BundleConfig.pb base
)
expect_rejection "managedAssetDirectory does not match" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/bad-contract-root.aab" \
    Different.Contract.dll
printf '%s\n' '{"assemblyName":"Different.Contract","abiVersion":2,"requiredFeatureBits":15,"schemaHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","managedAssetDirectory":"netlift/apps/com.netlift.sample/dotnet"}' \
    > "$other_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$other_bundle"
    zip -qr "$scratch/bad-contract-abi.aab" BundleConfig.pb base
)
expect_rejection "abiVersion must be 1" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/bad-contract-abi.aab" \
    Different.Contract.dll
printf '%s\n' '{"assemblyName":"Different.Contract","abiVersion":1,"requiredFeatureBits":0,"schemaHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","managedAssetDirectory":"netlift/apps/com.netlift.sample/dotnet"}' \
    > "$other_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$other_bundle"
    zip -qr "$scratch/bad-contract-features.aab" BundleConfig.pb base
)
expect_rejection "requiredFeatureBits must be a nonzero uint32" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/bad-contract-features.aab" \
    Different.Contract.dll
python3 - "$other_bundle/base/assets/netlift/apps/$app/contract.json" <<'PY'
import sys

with open(sys.argv[1], "w") as manifest:
    manifest.write('{"padding":"' + "x" * 65536 + '"}')
PY
(
    cd "$other_bundle"
    zip -qr "$scratch/oversized-contract.aab" BundleConfig.pb base
)
expect_rejection "contract manifest exceeds the 64 KiB limit" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/oversized-contract.aab" \
    Different.Contract.dll
printf '%s\n' '[0]' > "$other_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$other_bundle"
    zip -qr "$scratch/nonobject-contract.aab" BundleConfig.pb base
)
expect_rejection "contract manifest must be a JSON object" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/nonobject-contract.aab" \
    Different.Contract.dll
expect_rejection "required AAB structure missing" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/valid-generic.apk"

rm "$generic_bundle/base/assets/netlift/apps/$app/contract.json"
(
    cd "$generic_bundle"
    zip -qr "$scratch/generic-missing-contract.aab" BundleConfig.pb base
)
expect_rejection "app contract manifest missing" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/generic-missing-contract.aab"
cp "$generic/assets/netlift/apps/$app/contract.json" \
    "$generic_bundle/base/assets/netlift/apps/$app/contract.json"

rm "$generic_bundle/base/lib/arm64-v8a/libnetlift_androidruntime.so"
(
    cd "$generic_bundle"
    zip -qr "$scratch/generic-missing-host.aab" BundleConfig.pb base
)
expect_rejection "libnetlift_androidruntime.so" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/generic-missing-host.aab"
touch "$generic_bundle/base/lib/arm64-v8a/libnetlift_androidruntime.so"

mkdir -p "$generic_bundle/base/assets/netlift/runtime/11.0.1/dotnet"
touch "$generic_bundle/base/assets/netlift/runtime/11.0.1/dotnet/Extra.dll"
(
    cd "$generic_bundle"
    zip -qr "$scratch/generic-mixed-runtime.aab" BundleConfig.pb base
)
expect_rejection "expected one managed runtime version" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/generic-mixed-runtime.aab"
rm "$generic_bundle/base/assets/netlift/runtime/11.0.1/dotnet/Extra.dll"

mkdir -p "$generic_bundle/base/assets/netlift/apps/another.app/dotnet"
touch "$generic_bundle/base/assets/netlift/apps/another.app/dotnet/Extra.dll"
(
    cd "$generic_bundle"
    zip -qr "$scratch/generic-multiple-apps.aab" BundleConfig.pb base
)
expect_rejection "expected one app-specific managed asset root" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/generic-multiple-apps.aab"
rm "$generic_bundle/base/assets/netlift/apps/another.app/dotnet/Extra.dll"

touch "$generic_bundle/base/assets/Orders.Business.dll"
(
    cd "$generic_bundle"
    zip -qr "$scratch/generic-misplaced.aab" BundleConfig.pb base
)
expect_rejection "managed assemblies outside versioned runtime or app asset roots" \
    bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/generic-misplaced.aab"

if command -v python3 >/dev/null; then
    python3 - "$scratch/valid-generic.aab" "$scratch/generic-duplicate.aab" <<'PY'
import sys
import warnings
import zipfile

with zipfile.ZipFile(sys.argv[1]) as source, zipfile.ZipFile(sys.argv[2], "w") as target:
    for entry in source.infolist():
        target.writestr(entry, source.read(entry))
    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", message="Duplicate name: .*")
        target.writestr("base/lib/arm64-v8a/libcoreclr.so", b"duplicate")
PY
    expect_rejection "duplicate AAB archive entries" \
        bash "$root/tools/inspect-mobile-package.sh" android-generic-aab "$scratch/generic-duplicate.aab"
fi

mkdir -p "$scratch/bundle/base/assets/dotnet" "$scratch/bundle/base/lib/arm64-v8a" \
    "$scratch/bundle/base/manifest"
touch "$scratch/bundle/BundleConfig.pb" "$scratch/bundle/base/manifest/AndroidManifest.xml"
touch "$scratch/bundle/base/assets/dotnet/"{Orders.Business,NetLift.Managed,System.Private.CoreLib}.dll
touch "$scratch/bundle/base/lib/arm64-v8a/"{libcoreclr,libclrjit,libnetlift_host}.so
(
    cd "$scratch/bundle"
    zip -qr "$scratch/valid.aab" base BundleConfig.pb
)
bash "$root/tools/inspect-mobile-package.sh" android-aab "$scratch/valid.aab"
expect_rejection "required AAB structure missing" \
    bash "$root/tools/inspect-mobile-package.sh" android-aab "$scratch/valid.apk"

rm "$scratch/bundle/BundleConfig.pb"
(
    cd "$scratch/bundle"
    zip -qr "$scratch/missing-config.aab" base
)
expect_rejection "BundleConfig.pb" \
    bash "$root/tools/inspect-mobile-package.sh" android-aab "$scratch/missing-config.aab"
touch "$scratch/bundle/BundleConfig.pb"

rm "$scratch/bundle/base/manifest/AndroidManifest.xml"
(
    cd "$scratch/bundle"
    zip -qr "$scratch/missing-manifest.aab" base BundleConfig.pb
)
expect_rejection "base/manifest/AndroidManifest.xml" \
    bash "$root/tools/inspect-mobile-package.sh" android-aab "$scratch/missing-manifest.aab"
touch "$scratch/bundle/base/manifest/AndroidManifest.xml"

mkdir -p "$scratch/bundle/feature/assets/dotnet"
touch "$scratch/bundle/feature/assets/dotnet/Orders.Business.dll"
(
    cd "$scratch/bundle"
    zip -qr "$scratch/invalid-module.aab" base feature BundleConfig.pb
)
expect_rejection "managed assemblies outside base/assets/dotnet/" \
    bash "$root/tools/inspect-mobile-package.sh" android-aab "$scratch/invalid-module.aab"
rm "$scratch/bundle/feature/assets/dotnet/Orders.Business.dll"

rm "$scratch/bundle/base/lib/arm64-v8a/libclrjit.so"
(
    cd "$scratch/bundle"
    zip -qr "$scratch/missing-jit.aab" base BundleConfig.pb
)
expect_rejection "base/lib/arm64-v8a/libclrjit.so" \
    bash "$root/tools/inspect-mobile-package.sh" android-aab "$scratch/missing-jit.aab"
touch "$scratch/bundle/base/lib/arm64-v8a/libclrjit.so"
rm "$scratch/bundle/base/lib/arm64-v8a/libnetlift_host.so"
(
    cd "$scratch/bundle"
    zip -qr "$scratch/missing-host.aab" base BundleConfig.pb
)
expect_rejection "base/lib/arm64-v8a/libnetlift_host.so" \
    bash "$root/tools/inspect-mobile-package.sh" android-aab "$scratch/missing-host.aab"

touch "$scratch/android/assets/Orders.Business.dll"
(
    cd "$scratch/android"
    zip -qr "$scratch/duplicate.apk" assets lib
)
expect_rejection "managed assemblies outside assets/dotnet/" \
    bash "$root/tools/inspect-mobile-package.sh" android "$scratch/duplicate.apk"
rm "$scratch/android/assets/Orders.Business.dll"

rm "$scratch/android/assets/dotnet/System.Private.CoreLib.dll"
(
    cd "$scratch/android"
    zip -qr "$scratch/missing-corelib.apk" assets lib
)
expect_rejection "System.Private.CoreLib.dll" \
    bash "$root/tools/inspect-mobile-package.sh" android "$scratch/missing-corelib.apk"

mkdir -p "$scratch/NativeSwiftHost.app/Frameworks/libcoreclr.framework"
touch "$scratch/NativeSwiftHost.app/"{Orders.Business,NetLift.ManagedBridge,System.Private.CoreLib}.dll
touch "$scratch/NativeSwiftHost.app/Frameworks/libcoreclr.framework/libcoreclr"
bash "$root/tools/inspect-mobile-package.sh" ios "$scratch/NativeSwiftHost.app"
mv "$scratch/NativeSwiftHost.app/NetLift.ManagedBridge.dll" \
    "$scratch/NativeSwiftHost.app/NetLift.ManagedBridge.Typed.dll"
bash "$root/tools/inspect-mobile-package.sh" ios "$scratch/NativeSwiftHost.app" \
    NetLift.ManagedBridge.Typed.dll
expect_rejection "NetLift.ManagedBridge.dll" \
    bash "$root/tools/inspect-mobile-package.sh" ios "$scratch/NativeSwiftHost.app"
expect_rejection "bridge assembly must be a DLL filename" \
    bash "$root/tools/inspect-mobile-package.sh" ios "$scratch/NativeSwiftHost.app" \
    ../NetLift.ManagedBridge.Typed.dll
mv "$scratch/NativeSwiftHost.app/NetLift.ManagedBridge.Typed.dll" \
    "$scratch/NativeSwiftHost.app/NetLift.ManagedBridge.dll"

other_app="$scratch/OtherSwiftHost.app"
cp -R "$scratch/NativeSwiftHost.app" "$other_app"
mv "$other_app/Orders.Business.dll" "$other_app/Other.Business.dll"
mv "$other_app/NetLift.ManagedBridge.dll" "$other_app/Other.Bridge.dll"
bash "$root/tools/inspect-mobile-package.sh" ios "$other_app" \
    Other.Bridge.dll Other.Business.dll
expect_rejection "Orders.Business.dll" \
    bash "$root/tools/inspect-mobile-package.sh" ios "$other_app" \
    Other.Bridge.dll
expect_rejection "business assembly must be a DLL filename" \
    bash "$root/tools/inspect-mobile-package.sh" ios "$other_app" \
    Other.Bridge.dll ../Other.Business.dll

mkdir -p "$scratch/NativeSwiftHost.app/Frameworks/extra"
touch "$scratch/NativeSwiftHost.app/Frameworks/extra/Orders.Business.dll"
expect_rejection "duplicate managed assembly names" \
    bash "$root/tools/inspect-mobile-package.sh" ios "$scratch/NativeSwiftHost.app"
rm "$scratch/NativeSwiftHost.app/Frameworks/extra/Orders.Business.dll"

rm "$scratch/NativeSwiftHost.app/System.Private.CoreLib.dll"
expect_rejection "System.Private.CoreLib.dll" \
    bash "$root/tools/inspect-mobile-package.sh" ios "$scratch/NativeSwiftHost.app"

echo "PASS mobile APK/AAB/AAR/app auditor valid, mislocated and missing-runtime/host cases."
