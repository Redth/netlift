#!/bin/sh
set -eu

if [ "$#" -ne 3 ]; then
    echo "usage: stage-stream-v4.sh CONFIGURATION PLATFORM_NAME APP_BUNDLE_RESOURCES" >&2
    exit 64
fi

configuration=$1
platform=$2
bundle_resources=$3

case "$configuration" in
    Debug|Release) ;;
    *)
        echo "Unsupported stream bridge configuration: $configuration" >&2
        exit 64
        ;;
esac

if [ "$platform" != "iphonesimulator" ]; then
    echo "The v4 stream bridge is simulator-only." >&2
    exit 65
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
managed_project="$script_dir/managed/NetLift.ManagedBridge.StreamV4.csproj"
managed_output="$script_dir/managed/bin/$configuration/net11.0"

run_dotnet() {
    env -i \
        PATH="$PATH" \
        HOME="$HOME" \
        TMPDIR="${TMPDIR:-/tmp}" \
        DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}" \
        dotnet "$@"
}

run_dotnet build "$managed_project" -c "$configuration" -f net11.0

if [ ! -f "$managed_output/NetLift.ManagedBridge.StreamV4.dll" ]; then
    echo "The v4 stream managed bridge was not produced at $managed_output." >&2
    exit 66
fi

mkdir -p "$bundle_resources"
cp "$managed_output/NetLift.ManagedBridge.StreamV4.dll" "$bundle_resources/"
