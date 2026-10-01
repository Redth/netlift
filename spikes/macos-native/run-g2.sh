#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

build_root="spikes/macos-native/build/g2"
artifact_root="$build_root/artifacts"
projection_output="$build_root/projection"
managed_project="spikes/macos-native/CoreCLRHostG2.Managed/CoreCLRHostG2.Managed.csproj"
consumer_package="spikes/macos-native/CoreCLRHostG2Consumer"
generated_host="$consumer_package/build/generated-host"
generated_swift="$consumer_package/build/generated-swift"
app_bundle="spikes/macos-native/build/CoreCLRHostG2.app"
orders_schema_hash="c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4"

rm -rf "$build_root" "$app_bundle" "$generated_host" "$generated_swift"

for package_manifest in \
  spikes/macos-native/CoreCLRHostConsumer/Package.swift \
  "$consumer_package/Package.swift"; do
  grep -F '.package(name: "CoreCLRHost", path: "../CoreCLRHost")' \
    "$package_manifest" >/dev/null
done
host_package_hash="$(
  shasum -a 256 \
    spikes/macos-native/CoreCLRHost/Package.swift \
    spikes/macos-native/CoreCLRHost/Sources/CoreCLRHost/CoreCLRHost.swift \
    spikes/macos-native/CoreCLRHost/Sources/CoreCLRHostABI/CoreCLRHostABI.cpp \
    spikes/macos-native/CoreCLRHost/Sources/CoreCLRHostABI/include/CoreCLRHostABI.h \
    | shasum -a 256 | awk '{ print $1 }'
)"
echo "Shared CoreCLRHost source manifest SHA-256: $host_package_hash"

dotnet build "$managed_project" \
  -c Release --artifacts-path "$artifact_root" --nologo
dotnet build src/NetLift.Projection/NetLift.Projection.csproj \
  -c Release --artifacts-path "$artifact_root" --nologo

dotnet_path="$(command -v dotnet)"
dotnet_root="$(cd "$(dirname "$dotnet_path")" && pwd -P)"
runtime_version="$(dotnet --list-runtimes | awk \
  '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\.0\.0-rc\.1/ { print $2; exit }')"
if [[ -z "$runtime_version" ]]; then
  echo "Required .NET 11 RC 1 runtime is not installed." >&2
  exit 1
fi

case "$(uname -m)" in
  arm64) runtime_id="osx-arm64" ;;
  x86_64) runtime_id="osx-x64" ;;
  *)
    echo "Unsupported native macOS architecture: $(uname -m)" >&2
    exit 1
    ;;
esac
host_pack="$dotnet_root/packs/Microsoft.NETCore.App.Host.$runtime_id/$runtime_version/runtimes/$runtime_id/native"
if [[ ! -f "$host_pack/libnethost.a" ]]; then
  echo "Could not find the $runtime_id .NET host pack at $host_pack." >&2
  exit 1
fi
export NETLIFT_DOTNET_HOST_PACK="$host_pack"

generated_contract="$artifact_root/obj/CoreCLRHostG2.Managed/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
generated_host_module="$artifact_root/obj/CoreCLRHostG2.Managed/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftHostModule.g.cs"
if [[ ! -f "$generated_contract" || ! -f "$generated_host_module" ]]; then
  echo "The G2 contract generator did not emit its contract and host module." >&2
  exit 1
fi
dotnet "$artifact_root/bin/NetLift.Projection/release/NetLift.Projection.dll" \
  "$generated_contract" "$projection_output"

schema_hash="$(sed -n \
  's/^#define LIFT_SCHEMA_HASH_HEX "\([[:xdigit:]]*\)"/\1/p' \
  "$projection_output/netlift.h" | tr '[:upper:]' '[:lower:]')"
if [[ ${#schema_hash} -ne 64 || "$schema_hash" == "$orders_schema_hash" ]]; then
  echo "Expected a distinct 32-byte G2 schema hash; got '$schema_hash'." >&2
  exit 1
fi
grep -F '\"schemaVersion\":2' "$generated_contract" >/dev/null
grep -F 'DescribeV1' "$generated_host_module" >/dev/null
grep -F 'RegisterV1' "$generated_host_module" >/dev/null
grep -F 'Dispatch_41_7' "$generated_host_module" >/dev/null
echo "G2 schema hash: $schema_hash"
echo "Orders schema hash: $orders_schema_hash"

mkdir -p "$generated_host/include" "$generated_swift"
cp "$projection_output/netlift.h" "$generated_host/include/"
cp "$projection_output/NetLift.swift" "$generated_swift/NetLift.swift"
cat > "$generated_host/hostabi.c" <<'EOF'
#include "netlift.h"
EOF

swift build \
  --package-path "$consumer_package" \
  --scratch-path "$build_root/swiftpm" \
  --configuration release
swift_bin="$(swift build \
  --package-path "$consumer_package" \
  --scratch-path "$build_root/swiftpm" \
  --configuration release \
  --show-bin-path)"

# Restore the matching runtime pack only; the hosted library uses its normal
# framework-dependent runtimeconfig.json with the private framework layout.
dotnet publish "$managed_project" \
  -c Release \
  -r "$runtime_id" \
  --self-contained true \
  --artifacts-path "$artifact_root" \
  -o "$build_root/runtime-pack-stage" \
  --nologo

nuget_root="$(dotnet nuget locals global-packages --list | sed 's/^[^:]*: //')"
runtime_package="$nuget_root/microsoft.netcore.app.runtime.$runtime_id/$runtime_version"
runtime_assets="$runtime_package/runtimes/$runtime_id"
if [[ ! -f "$runtime_assets/native/libhostfxr.dylib" ||
      ! -f "$runtime_assets/native/libcoreclr.dylib" ||
      ! -f "$runtime_assets/native/libhostpolicy.dylib" ||
      ! -f "$runtime_assets/lib/net11.0/System.Private.CoreLib.dll" ||
      ! -f "$runtime_assets/lib/net11.0/Microsoft.NETCore.App.deps.json" ]]; then
  echo "The matching .NET $runtime_version runtime pack is incomplete at $runtime_assets." >&2
  exit 1
fi

app_resources="$app_bundle/Contents/Resources"
bundle_dotnet_root="$app_resources/dotnet"
bundle_framework="$bundle_dotnet_root/shared/Microsoft.NETCore.App/$runtime_version"
bundle_hostfxr="$bundle_dotnet_root/host/fxr/$runtime_version"
managed_output="$artifact_root/bin/CoreCLRHostG2.Managed/release"
mkdir -p "$app_bundle/Contents/MacOS" "$app_resources/managed" \
  "$bundle_framework" "$bundle_hostfxr"
cp "$swift_bin/CoreCLRHostG2Consumer" \
  "$app_bundle/Contents/MacOS/CoreCLRHostG2Consumer"
cp "$managed_output"/* "$app_resources/managed/"
cp -R "$runtime_assets/lib/net11.0/." "$bundle_framework/"
cp -R "$runtime_assets/native/." "$bundle_framework/"
cp "$runtime_assets/native/libhostfxr.dylib" "$bundle_hostfxr/"
cp "$runtime_package/LICENSE.TXT" "$app_resources/"
cp "$runtime_package/THIRD-PARTY-NOTICES.TXT" "$app_resources/"
cmp "$runtime_assets/native/libhostfxr.dylib" "$bundle_hostfxr/libhostfxr.dylib"
cmp "$runtime_assets/native/libcoreclr.dylib" "$bundle_framework/libcoreclr.dylib"
shasum -a 256 "$bundle_hostfxr/libhostfxr.dylib" \
  "$bundle_framework/libcoreclr.dylib"

cat > "$app_bundle/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>CoreCLRHostG2Consumer</string>
  <key>CFBundleIdentifier</key>
  <string>dev.netlift.macos-native.coreclr-host-g2</string>
  <key>CFBundleName</key>
  <string>CoreCLR Host G2 Consumer</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>LSMinimumSystemVersion</key>
  <string>14.0</string>
</dict>
</plist>
EOF

codesign --force --deep --sign - --timestamp=none "$app_bundle"
codesign --verify --deep --strict --verbose=2 "$app_bundle"
plutil -lint "$app_bundle/Contents/Info.plist"

consumer_log="$build_root/consumer-bundled.log"
env -i \
  HOME="$HOME" \
  PATH=/usr/bin:/bin \
  DOTNET_ROOT="$bundle_dotnet_root" \
  DOTNET_MULTILEVEL_LOOKUP=0 \
  DYLD_PRINT_LIBRARIES=1 \
  "$app_bundle/Contents/MacOS/CoreCLRHostG2Consumer" \
  "$app_resources/managed" "$schema_hash" > "$consumer_log" 2>&1
grep -F "$bundle_hostfxr/libhostfxr.dylib" "$consumer_log" >/dev/null
grep -F "$bundle_framework/libcoreclr.dylib" "$consumer_log" >/dev/null
grep -F "PASS distinct G2 schema $schema_hash != Orders $orders_schema_hash" \
  "$consumer_log"
grep -F "PASS descriptor mismatch rejected before factory or route publication" \
  "$consumer_log"
grep -F "PASS generated v2 DTO request -> managed service -> Cdecl Swift callback -> typed response" \
  "$consumer_log"
echo "PASS G2 consumer loaded hostfxr/CoreCLR only from its separate app payload"
file "$app_bundle/Contents/MacOS/CoreCLRHostG2Consumer"
du -sh "$app_bundle"
