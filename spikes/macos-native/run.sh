#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

app_name="NetLiftMacOS"
app_bundle="spikes/macos-native/build/${app_name}.app"
artifact_root="spikes/macos-native/build/artifacts"
managed_output="$artifact_root/bin/NetLift.MacBridge/release"
generated_managed_output="$artifact_root/bin/NetLift.NativeHost.Managed/release"
generated_source="$artifact_root/obj/NetLift.NativeHost.Managed/generated/NetLift.Generator/NetLift.Generator.LiftContractGenerator/LiftContract.g.cs"
projection_output="spikes/macos-native/build/projection"
generated_host_module="spikes/macos-native/build/generated-host"
generated_swift_module="spikes/macos-native/build/generated-swift"
v2_schema_hash="c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4"
mode="${1:-launch}"

case "$mode" in
  --smoke|--build-only|--bundled-smoke|launch) ;;
  *)
    echo "Usage: bash spikes/macos-native/run.sh [--smoke|--build-only|--bundled-smoke]" >&2
    exit 2
    ;;
esac

dotnet build spikes/macos-native/NetLift.MacBridge.csproj \
  -c Release --artifacts-path "$artifact_root" --nologo
dotnet build tests/NetLift.NativeHost.Managed/NetLift.NativeHost.Managed.csproj \
  -c Release --artifacts-path "$artifact_root" --nologo
dotnet build src/NetLift.Projection/NetLift.Projection.csproj \
  -c Release --artifacts-path "$artifact_root" --nologo

dotnet_path="$(command -v dotnet)"
dotnet_root="$(cd "$(dirname "$dotnet_path")" && pwd -P)"
runtime_version="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\.0\.0-rc\.1/ { print $2; exit }')"
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

mkdir -p "$projection_output" "$generated_host_module/include" "$generated_swift_module"
dotnet "$artifact_root/bin/NetLift.Projection/release/NetLift.Projection.dll" \
  "$generated_source" "$projection_output"
cp "$projection_output/NetLift.swift" "$generated_swift_module/NetLift.swift"
cp "$projection_output/netlift.h" "$generated_host_module/include/"
cp spikes/macos-native/NativeHostModule/include/NetLiftGeneratedHost.h \
  "$generated_host_module/include/"
generated_schema_hash="$(sed -n 's/^#define LIFT_SCHEMA_HASH_HEX "\([[:xdigit:]]*\)"/\1/p' \
  "$generated_host_module/include/netlift.h")"
if [[ "$generated_schema_hash" != "$v2_schema_hash" ]]; then
  echo "Expected default v2 schema $v2_schema_hash, got $generated_schema_hash." >&2
  exit 1
fi
echo "Pinned generated v2 schema: $generated_schema_hash"
cat > "$generated_host_module/hostabi.c" <<'EOF'
#include "netlift.h"
#include "NetLiftGeneratedHost.h"
EOF

clang++ -std=c++17 -Wall -Wextra -Werror \
  -mmacosx-version-min=14.0 \
  -I "$host_pack" \
  -I spikes/macos-native/CoreCLRHost/Sources/CoreCLRHostABI/include \
  -I "$generated_host_module/include" \
  -I spikes/macos-native/NativeHostModule/include \
  -c spikes/macos-native/host.cpp \
  -o spikes/macos-native/build/host.o
clang++ -std=c++20 -Wall -Wextra -Werror \
  -mmacosx-version-min=14.0 \
  -I "$generated_host_module/include" \
  -I spikes/macos-native/NativeHostModule/include \
  -c spikes/macos-native/generated_business_host.cpp \
  -o spikes/macos-native/build/generated_business_host.o
host_object="$(pwd)/spikes/macos-native/build/host.o"
business_host_object="$(pwd)/spikes/macos-native/build/generated_business_host.o"
swift build \
  --package-path spikes/macos-native \
  --scratch-path spikes/macos-native/build/swiftpm \
  --configuration release \
  -Xlinker "$host_object" \
  -Xlinker "$business_host_object"
swift_bin="$(swift build \
  --package-path spikes/macos-native \
  --scratch-path spikes/macos-native/build/swiftpm \
  --configuration release \
  --show-bin-path)"
swift build \
  --package-path spikes/macos-native/CoreCLRHostConsumer \
  --scratch-path spikes/macos-native/build/coreclr-host-consumer \
  --configuration release
consumer_swift_bin="$(swift build \
  --package-path spikes/macos-native/CoreCLRHostConsumer \
  --scratch-path spikes/macos-native/build/coreclr-host-consumer \
  --configuration release \
  --show-bin-path)"

if [[ "$mode" == "--smoke" ]]; then
  DOTNET_ROOT="$dotnet_root" "$swift_bin/NetLiftNativeSmoke" "$managed_output"
  DOTNET_ROOT="$dotnet_root" "$consumer_swift_bin/CoreCLRHostConsumer" "$managed_output"
  exit 0
fi

staging_output="spikes/macos-native/build/runtime-pack-stage"
dotnet publish spikes/macos-native/NetLift.MacBridge.csproj \
  -c Release \
  -r "$runtime_id" \
  --self-contained true \
  --artifacts-path "$artifact_root" \
  -o "$staging_output" \
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
mkdir -p "$app_bundle/Contents/MacOS" \
  "$app_resources/managed" "$bundle_framework" "$bundle_hostfxr"
cp "$swift_bin/$app_name" "$app_bundle/Contents/MacOS/$app_name"
cp "$managed_output"/* "$app_bundle/Contents/Resources/managed/"
mkdir -p "$app_resources/generated-managed"
cp "$generated_managed_output"/* "$app_resources/generated-managed/"
cp -R "$runtime_assets/lib/net11.0/." "$bundle_framework/"
cp -R "$runtime_assets/native/." "$bundle_framework/"
cp "$runtime_assets/native/libhostfxr.dylib" "$bundle_hostfxr/"
cp "$runtime_package/LICENSE.TXT" "$app_resources/"
cp "$runtime_package/THIRD-PARTY-NOTICES.TXT" "$app_resources/"

cat > "$app_bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>$app_name</string>
  <key>CFBundleIdentifier</key>
  <string>dev.netlift.macos-native-spike</string>
  <key>CFBundleName</key>
  <string>NetLift macOS Native Spike</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>LSEnvironment</key>
  <dict>
    <key>DOTNET_ROOT</key>
    <string>$bundle_dotnet_root</string>
    <key>DOTNET_MULTILEVEL_LOOKUP</key>
    <string>0</string>
  </dict>
  <key>LSMinimumSystemVersion</key>
  <string>14.0</string>
</dict>
</plist>
EOF

codesign --force --deep --sign - --timestamp=none "$app_bundle"
codesign --verify --deep --strict --verbose=2 "$app_bundle"
echo "Built native SwiftUI app: $app_bundle"
echo "Bundled runtime: .NET $runtime_version, $runtime_id (framework-dependent hostfxr config)"
du -sh "$app_bundle"
if [[ "${1:-}" == "--build-only" ]]; then
  exit 0
fi
if [[ "$mode" == "--bundled-smoke" ]]; then
  env -i \
    HOME="$HOME" \
    PATH=/usr/bin:/bin \
    DOTNET_ROOT="$bundle_dotnet_root" \
    DOTNET_MULTILEVEL_LOOKUP=0 \
    "$swift_bin/NetLiftNativeSmoke" "$app_resources/managed"
  env -i \
    HOME="$HOME" \
    PATH=/usr/bin:/bin \
    DOTNET_ROOT="$bundle_dotnet_root" \
    DOTNET_MULTILEVEL_LOOKUP=0 \
    "$swift_bin/NetLiftGeneratedSmoke" "$app_resources/generated-managed"
  env -i \
    HOME="$HOME" \
    PATH=/usr/bin:/bin \
    DOTNET_ROOT="$bundle_dotnet_root" \
    DOTNET_MULTILEVEL_LOOKUP=0 \
    "$swift_bin/NetLiftBusinessSmoke" "$app_resources/managed"
  consumer_log="spikes/macos-native/build/coreclr-host-consumer.log"
  env -i \
    HOME="$HOME" \
    PATH=/usr/bin:/bin \
    DOTNET_ROOT="$bundle_dotnet_root" \
    DOTNET_MULTILEVEL_LOOKUP=0 \
    DYLD_PRINT_LIBRARIES=1 \
    "$consumer_swift_bin/CoreCLRHostConsumer" "$app_resources/managed" \
    > "$consumer_log" 2>&1
  grep -F "$bundle_hostfxr/libhostfxr.dylib" "$consumer_log" >/dev/null
  grep -F "$bundle_framework/libcoreclr.dylib" "$consumer_log" >/dev/null
  grep -F "PASS independent SwiftPM consumer loaded managed .NET 11 probe" \
    "$consumer_log"
  grep -F "PASS 32 concurrent CoreCLR handle load/release and stale-handle races" \
    "$consumer_log"
  echo "PASS independent SwiftPM consumer loaded only the app-bundled hostfxr/CoreCLR"
  exit 0
fi
open "$app_bundle"
