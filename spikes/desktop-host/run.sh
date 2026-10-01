#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."
dotnet build spikes/desktop-host/NetLift.DesktopBridge/NetLift.DesktopBridge.csproj -c Release --nologo

dotnet_root="$(dirname "$(command -v dotnet)")"
runtime_version="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^11\.0\.0-rc\.1/ { print $2; exit }')"
if [[ -z "$runtime_version" ]]; then
  echo "Required .NET 11 RC1 runtime not installed." >&2
  exit 1
fi

native="$dotnet_root/packs/Microsoft.NETCore.App.Host.osx-arm64/$runtime_version/runtimes/osx-arm64/native"
output="spikes/desktop-host/NetLift.DesktopBridge/bin/Release/net11.0"
clang++ -std=c++17 -Wall -Wextra -Werror -I "$native" \
  spikes/desktop-host/host.cpp "$native/libnethost.a" -ldl \
  -o spikes/desktop-host/host
DOTNET_ROOT="$dotnet_root" spikes/desktop-host/host "$output"
