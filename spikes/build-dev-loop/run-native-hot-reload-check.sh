#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
cd "$repo_root"
spike_root="$repo_root/spikes/build-dev-loop"
native_spike="$spike_root/NativeHotReload"
artifacts="$spike_root/.artifacts/native-hot-reload"
logs="$artifacts/logs"
dotnet_root="$(cd "$(dirname "$(command -v dotnet)")" && pwd -P)"
runtime_version="$(dotnet --list-runtimes |
    awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\.0\.0-rc\.1/ { print $2; exit }')"
if [[ -z "$runtime_version" ]]; then
    echo "Pinned .NET 11 RC1 runtime is not installed." >&2
    exit 1
fi
native_pack="$dotnet_root/packs/Microsoft.NETCore.App.Host.osx-arm64/$runtime_version/runtimes/osx-arm64/native"
reference_pack="$dotnet_root/packs/Microsoft.NETCore.App.Ref/$runtime_version/ref/net11.0"
if [[ ! -f "$native_pack/libnethost.a" || ! -d "$reference_pack" ]]; then
    echo "Required .NET 11 RC1 native/reference packs are unavailable." >&2
    exit 1
fi

rm -rf "$artifacts/api-probe" "$artifacts/bridge" "$artifacts/delta-tool" \
    "$artifacts/deltas" "$artifacts/native-host" "$logs"
mkdir -p "$logs" "$artifacts/dotnet-home" "$artifacts/nuget-packages"
{
    printf 'dotnet --version: '
    dotnet --version
    printf 'Microsoft.NETCore.App: %s\n' "$runtime_version"
    printf 'clang++: '
    clang++ --version | sed -n '1p'
    printf 'Native host pack: %s\n' "$native_pack"
    printf 'Reference pack: %s\n' "$reference_pack"
    echo "DOTNET_MODIFIABLE_ASSEMBLIES=debug (set before hostfxr initialization)"
} > "$logs/tool-versions.log"
export DOTNET_ROOT="$dotnet_root"
export DOTNET_CLI_HOME="$artifacts/dotnet-home"
export NUGET_PACKAGES="$artifacts/nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! dotnet build "$native_spike/HostBridge/HostBridge.csproj" \
    -c Debug --artifacts-path "$artifacts/bridge" --nologo \
    > "$logs/bridge-build.log" 2>&1; then
    tail -n 100 "$logs/bridge-build.log" >&2
    exit 1
fi
if ! dotnet build "$native_spike/DeltaGenerator/DeltaGenerator.csproj" \
    -c Debug --artifacts-path "$artifacts/delta-tool" --nologo \
    > "$logs/delta-generator-build.log" 2>&1; then
    tail -n 100 "$logs/delta-generator-build.log" >&2
    exit 1
fi

delta_generator="$artifacts/delta-tool/bin/DeltaGenerator/debug/DeltaGenerator.dll"
if ! dotnet "$delta_generator" \
    "$native_spike/Target/Probe.v1.cs" \
    "$native_spike/Target/Probe.v2.cs" \
    "$artifacts/deltas" \
    "$reference_pack" \
    > "$logs/delta-generation.log" 2>&1; then
    cat "$logs/delta-generation.log" >&2
    exit 1
fi

if ! clang++ -std=c++17 -Wall -Wextra -Werror \
    -I "$native_pack" \
    "$native_spike/host.cpp" \
    "$native_pack/libnethost.a" \
    -ldl \
    -o "$artifacts/native-host" \
    > "$logs/native-build.log" 2>&1; then
    cat "$logs/native-build.log" >&2
    exit 1
fi

bridge_output="$artifacts/bridge/bin/HostBridge/debug"
target_assembly="$artifacts/deltas/baseline/NetLift.HotReloadTarget.dll"
export NETLIFT_DELTA_DIRECTORY="$artifacts/deltas/delta"
if ! "$artifacts/native-host" "$bridge_output" "$target_assembly" \
    > "$logs/native-host.log" 2>&1; then
    cat "$logs/native-host.log" >&2
    exit 1
fi

cat "$logs/delta-generation.log"
grep -E 'METADATA_UPDATER_SUPPORTED=|OBSERVE_BEFORE=|METADATA-DELTA-APPLIED|OBSERVE_AFTER=|PASS native-hosted' \
    "$logs/native-host.log"
