#!/bin/sh
set -eu

if [ "$#" -ne 3 ]; then
    echo "usage: stage-runtime-typed.sh CONFIGURATION PLATFORM_NAME APP_BUNDLE_RESOURCES" >&2
    exit 64
fi

configuration=$1
platform=$2
bundle_resources=$3

case "$configuration" in
    Debug|Release) ;;
    *)
        echo "Unsupported typed .NET runtime configuration: $configuration" >&2
        exit 64
        ;;
esac

if [ "$platform" != "iphonesimulator" ]; then
    echo "Typed direct runtime-pack staging is simulator-only; device packaging is not implemented." >&2
    exit 65
fi

runtime_id=iossimulator-arm64
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
managed_output="$script_dir/.artifacts/managed/$runtime_id/$configuration"
artifacts_path="$script_dir/.artifacts/dotnet-artifacts"

run_dotnet() {
    env -i \
        PATH="$PATH" \
        HOME="$HOME" \
        TMPDIR="${TMPDIR:-/tmp}" \
        DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}" \
        dotnet "$@"
}

sdk_base_path=$(run_dotnet --info |
    sed -n 's/^[[:space:]]*Base Path:[[:space:]]*//p' | head -n 1)
if [ -z "$sdk_base_path" ]; then
    echo "Unable to determine the selected .NET SDK base path." >&2
    exit 66
fi

dotnet_root=${sdk_base_path%/sdk/*}
sdk_version=$(run_dotnet --version)
sdk_band=$(printf '%s\n' "$sdk_version" | sed -E 's/(\.[0-9]+){2}$//')
runtime_pack_version=$(sed -n \
    's/.*<BundledNETCoreAppPackageVersion>\([^<]*\)<\/BundledNETCoreAppPackageVersion>.*/\1/p' \
    "$sdk_base_path/Microsoft.NETCoreSdk.BundledVersions.props" | head -n 1)
ios_manifest_root="$dotnet_root/sdk-manifests/$sdk_band/microsoft.net.sdk.ios"
ios_manifest=$(find "$ios_manifest_root" -mindepth 2 -maxdepth 2 \
    -type f -name WorkloadManifest.json -print 2>/dev/null | sort -V | tail -n 1)
if [ -z "$runtime_pack_version" ] || [ -z "$ios_manifest" ]; then
    echo "Unable to resolve the selected .NET runtime or iOS workload manifest." >&2
    exit 66
fi

ios_pack_suffix=net11.0_26.5
ios_runtime_pack_name="Microsoft.iOS.Runtime.$runtime_id.$ios_pack_suffix"
ios_shared_pack_name="Microsoft.iOS.Runtime.ios.$ios_pack_suffix"
ios_pack_version=$(sed -n \
    "/\"$ios_runtime_pack_name\"[[:space:]]*:/,/}/ s/.*\"version\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" \
    "$ios_manifest" | head -n 1)
if [ -z "$ios_pack_version" ]; then
    echo "The selected iOS workload manifest does not declare $ios_runtime_pack_name." >&2
    exit 66
fi

nuget_root=$(run_dotnet nuget locals global-packages --list |
    sed -n 's/^global-packages: //p' | head -n 1)
runtime_pack="$nuget_root/microsoft.netcore.app.runtime.$runtime_id/$runtime_pack_version/runtimes/$runtime_id"
ios_runtime_pack="$dotnet_root/packs/$ios_runtime_pack_name/$ios_pack_version/runtimes/$runtime_id"
ios_shared_pack="$dotnet_root/packs/$ios_shared_pack_name/$ios_pack_version/runtimes/ios"

if [ ! -d "$runtime_pack/lib/net11.0" ] ||
    [ ! -f "$runtime_pack/native/libcoreclr.dylib" ] ||
    [ ! -f "$ios_runtime_pack/native/Microsoft.iOS.registrar.coreclr.a" ] ||
    [ ! -f "$ios_shared_pack/lib/net11.0/Microsoft.iOS.dll" ]; then
    echo "Resolved runtime packs are incomplete for $runtime_id." >&2
    exit 66
fi

run_dotnet build "$script_dir/TypedManagedBridge.csproj" \
    -c "$configuration" \
    -f net11.0 \
    --artifacts-path "$artifacts_path" \
    --output "$managed_output"

mkdir -p "$bundle_resources"
for source in "$runtime_pack"/lib/net11.0/*.dll; do
    [ -f "$source" ] || continue
    cp "$source" "$bundle_resources/"
done
cp "$ios_shared_pack/lib/net11.0/Microsoft.iOS.dll" "$bundle_resources/"
for source in "$runtime_pack"/native/lib*.dylib; do
    [ -f "$source" ] || continue
    cp "$source" "$bundle_resources/"
done

xamarin_runtime="$ios_runtime_pack/native/libxamarin-dotnet-coreclr.dylib"
if [ "$configuration" = "Debug" ]; then
    xamarin_runtime="$ios_runtime_pack/native/libxamarin-dotnet-coreclr-debug.dylib"
fi
if [ ! -f "$xamarin_runtime" ]; then
    echo "Missing CoreCLR iOS runtime shim: $xamarin_runtime" >&2
    exit 66
fi
cp "$xamarin_runtime" "$bundle_resources/"
cp "$managed_output/NetLift.ManagedBridge.Typed.dll" "$bundle_resources/"
cp "$managed_output/NetLift.Contracts.dll" "$bundle_resources/"
cp "$managed_output/Orders.Business.dll" "$bundle_resources/"

printf 'Staged isolated schema-v3 managed bridge and .NET %s/%s runtime assets into %s.\n' \
    "$runtime_pack_version" "$ios_pack_version" "$bundle_resources"
