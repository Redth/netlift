#!/bin/sh
set -eu

# Diagnostic comparison path; the Xcode project uses direct pack staging by default.
if [ "$#" -ne 3 ]; then
    echo "usage: stage-runtime-probe.sh CONFIGURATION PLATFORM_NAME APP_BUNDLE_RESOURCES" >&2
    exit 64
fi

configuration=$1
platform=$2
bundle_resources=$3
runtime_configuration=${RUNTIME_ASSET_CONFIGURATION:-$configuration}

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export DEVELOPER_DIR=${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}

case "$runtime_configuration" in
    Debug|Release) ;;
    *)
        echo "Unsupported .NET runtime asset configuration: $runtime_configuration" >&2
        exit 64
        ;;
esac

case "$platform" in
    iphonesimulator)
        runtime_id=iossimulator-arm64
        ;;
    iphoneos)
        runtime_id=ios-arm64
        ;;
    *)
        echo "Unsupported iOS platform for this spike: $platform." >&2
        exit 65
        ;;
esac

staging_project="$script_dir/.artifacts/staging/$runtime_id"
staging_app="$staging_project/bin/$runtime_configuration/net11.0-ios/$runtime_id/RuntimeAssetProbe.app"
managed_output="$script_dir/.artifacts/managed/$runtime_id/$configuration"

run_dotnet() {
    env -i \
        PATH="$PATH" \
        HOME="$HOME" \
        TMPDIR="${TMPDIR:-/tmp}" \
        DEVELOPER_DIR="$DEVELOPER_DIR" \
        dotnet "$@"
}

mkdir -p "$staging_project"
cat > "$staging_project/RuntimeAssetProbe.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0-ios</TargetFramework>
    <OutputType>Exe</OutputType>
    <AssemblyName>RuntimeAssetProbe</AssemblyName>
    <ApplicationId>org.netlift.apple.runtimeassetprobe</ApplicationId>
    <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
    <UseMonoRuntime>false</UseMonoRuntime>
    <TrimMode>copy</TrimMode>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
PROJECT
cat > "$staging_project/Program.cs" <<'CS'
using Foundation;
using UIKit;

UIApplication.Main(args, null, typeof(RuntimeAssetProbeAppDelegate));

[Register("RuntimeAssetProbeAppDelegate")]
public sealed class RuntimeAssetProbeAppDelegate : UIApplicationDelegate
{
}
CS

run_dotnet restore "$staging_project/RuntimeAssetProbe.csproj" -p:RuntimeIdentifier="$runtime_id"
if [ "$platform" = "iphoneos" ]; then
    # Device asset inspection must not attempt signing or provisioning.
    run_dotnet build "$staging_project/RuntimeAssetProbe.csproj" \
        -c "$runtime_configuration" \
        -f net11.0-ios \
        -r "$runtime_id" \
        -p:EnableCodeSigning=false \
        --no-restore
else
    run_dotnet build "$staging_project/RuntimeAssetProbe.csproj" \
        -c "$runtime_configuration" \
        -f net11.0-ios \
        -r "$runtime_id" \
        --no-restore
fi
run_dotnet build "$script_dir/managed/NetLift.ManagedBridge.csproj" \
    -c "$configuration" \
    -f net11.0 \
    --output "$managed_output"

if [ "$platform" = "iphoneos" ]; then
    coreclr_asset="$staging_app/Frameworks/libcoreclr.framework/libcoreclr"
else
    coreclr_asset="$staging_app/libcoreclr.dylib"
fi
if [ ! -f "$coreclr_asset" ] || [ ! -f "$staging_app/System.Private.CoreLib.dll" ]; then
    echo "The generated .NET runtime staging output is missing CoreCLR assets: $staging_app" >&2
    exit 66
fi
if [ -n "${RUNTIME_ASSET_CORELIB_OVERRIDE:-}" ] && [ ! -f "$RUNTIME_ASSET_CORELIB_OVERRIDE" ]; then
    echo "The CoreLib diagnostic override does not exist: $RUNTIME_ASSET_CORELIB_OVERRIDE" >&2
    exit 66
fi

mkdir -p "$bundle_resources"
for source in "$staging_app"/*.dll; do
    [ -f "$source" ] || continue
    case "$(basename "$source")" in
        RuntimeAssetProbe.dll) continue ;;
    esac
    cp "$source" "$bundle_resources/"
done
for source in "$staging_app"/lib*.dylib; do
    [ -f "$source" ] || continue
    cp "$source" "$bundle_resources/"
done
if [ -n "${RUNTIME_ASSET_CORELIB_OVERRIDE:-}" ]; then
    cp "$RUNTIME_ASSET_CORELIB_OVERRIDE" "$bundle_resources/System.Private.CoreLib.dll"
fi
if [ "$platform" = "iphoneos" ]; then
    mkdir -p "$bundle_resources/Frameworks"
    cp -R "$staging_app/Frameworks/." "$bundle_resources/Frameworks/"
fi

cp "$managed_output/NetLift.ManagedBridge.dll" "$bundle_resources/"
cp "$managed_output/NetLift.Contracts.dll" "$bundle_resources/"
cp "$managed_output/Orders.Business.dll" "$bundle_resources/"

printf 'Staged CoreCLR, %s runtime assemblies, and managed bridge into %s\n' \
    "$(find "$bundle_resources" -maxdepth 1 -name '*.dll' -type f | wc -l | tr -d ' ')" \
    "$bundle_resources"
