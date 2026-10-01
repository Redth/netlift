#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
scratch="${NETLIFT_NATIVEAOT_SCRATCH_DIR:-}"
# Online restore is opt-in and pinned to the official NuGet v3 endpoint below.
restore_mode="${NETLIFT_NATIVEAOT_RESTORE_MODE:-offline}"
skip_restore="${NETLIFT_NATIVEAOT_SKIP_RESTORE:-0}"

if [[ -z "$scratch" || "$scratch" != "$HOME"/.copilot/session-state/*/files/apple-nativeaot-macos-library ]]; then
  echo "Set NETLIFT_NATIVEAOT_SCRATCH_DIR to this session's files/apple-nativeaot-macos-library directory." >&2
  exit 64
fi

case "$restore_mode" in
  offline)
    restore_source="$scratch/offline-feed"
    ;;
  online)
    restore_source="https://api.nuget.org/v3/index.json"
    ;;
  *)
    echo "NETLIFT_NATIVEAOT_RESTORE_MODE must be 'offline' or 'online'." >&2
    exit 64
    ;;
esac

mkdir -p "$scratch"
scratch="$(cd "$scratch" && pwd -P)"
case "$scratch" in
  "$HOME"/.copilot/session-state/*/files/apple-nativeaot-macos-library) ;;
  *)
    echo "Refusing scratch outside a Copilot session files directory: $scratch" >&2
    exit 64
    ;;
esac

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "This probe requires macOS arm64." >&2
  exit 65
fi

export DOTNET_CLI_HOME="$scratch/cli-home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export TMPDIR="$scratch/tmp"
export NUGET_PACKAGES="$scratch/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$scratch/nuget-http-cache"
mkdir -p "$DOTNET_CLI_HOME" "$TMPDIR" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"
if [[ "$restore_mode" == "offline" ]]; then
  mkdir -p "$restore_source"
fi
if [[ "$skip_restore" != "0" && "$skip_restore" != "1" ]]; then
  echo "NETLIFT_NATIVEAOT_SKIP_RESTORE must be '0' or '1'." >&2
  exit 64
fi

sdk_version="$(dotnet --version)"
if [[ "$sdk_version" != "11.0.100-rc.1.26425.128" ]]; then
  echo "Expected .NET SDK 11.0.100-rc.1.26425.128; found $sdk_version." >&2
  exit 65
fi

if ! xcode_path="$(xcode-select -p 2>/dev/null)" || [[ ! -d "$xcode_path" ]]; then
  echo "A selected Xcode developer directory is required." >&2
  exit 65
fi

build_root="$scratch/build"
publish_root="$scratch/publish"
mkdir -p "$build_root" "$publish_root"

project="$script_dir/Probe/Probe.csproj"
log="$scratch/restore-${restore_mode}.log"
assets_file="$build_root/obj/project.assets.json"
echo "RESTORE_MODE=$restore_mode"
echo "RESTORE_SOURCE=$restore_source"
echo "NUGET_PACKAGES=$NUGET_PACKAGES"
echo "NUGET_HTTP_CACHE_PATH=$NUGET_HTTP_CACHE_PATH"
if [[ "$skip_restore" == "1" ]]; then
  if [[ ! -s "$assets_file" ]]; then
    echo "NETLIFT_NATIVEAOT_SKIP_RESTORE=1 requires existing project assets at $assets_file." >&2
    exit 65
  fi
  echo "RESTORE=SKIPPED_USING_EXISTING_ASSETS"
else
  set +e
  dotnet restore "$project" \
    --source "$restore_source" \
    --packages "$NUGET_PACKAGES" \
    -p:BaseIntermediateOutputPath="$build_root/obj/" \
    -p:BaseOutputPath="$build_root/bin/" \
    -p:MSBuildProjectExtensionsPath="$build_root/obj/" \
    -v:minimal 2>&1 | tee "$log"
  restore_status=${PIPESTATUS[0]}
  set -e

  if [[ "$restore_status" -ne 0 ]]; then
    if grep -q "error NU1101: Unable to find package" "$log"; then
      echo "RESULT=BLOCKED_MISSING_NATIVEAOT_PACKAGES"
      exit 2
    fi
    echo "RESULT=RESTORE_FAILED"
    exit "$restore_status"
  fi
fi

publish_log="$scratch/publish-${restore_mode}.log"
set +e
dotnet publish "$project" \
  -c Release \
  --no-restore \
  -p:BaseIntermediateOutputPath="$build_root/obj/" \
  -p:BaseOutputPath="$build_root/bin/" \
  -p:MSBuildProjectExtensionsPath="$build_root/obj/" \
  -o "$publish_root" \
  -v:minimal 2>&1 | tee "$publish_log"
publish_status=${PIPESTATUS[0]}
set -e

if [[ "$publish_status" -ne 0 ]]; then
  echo "RESULT=PUBLISH_FAILED"
  exit "$publish_status"
fi

native_library="$build_root/bin/Release/net11.0/osx-arm64/native/libProbe.dylib"
if [[ ! -f "$native_library" ]]; then
  echo "RESULT=PUBLISH_COMPLETED_WITHOUT_EXPECTED_DYLIB"
  exit 3
fi

for tool in codesign ditto file grep nm otool security sed swiftc tee xcrun; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "Required tool is unavailable: $tool" >&2
    exit 65
  fi
done

identity_line="$(security find-identity -v -p codesigning 2>/dev/null |
  awk '/"Developer ID Application:/ { print; found = 1; exit } END { if (!found) exit 1 }')" || {
  echo "A valid installed Developer ID Application identity is required." >&2
  exit 2
}
signing_identity="$(printf '%s\n' "$identity_line" | awk '{ print $2 }')"
signing_team="$(printf '%s\n' "$identity_line" |
  sed -E 's/.*\(([A-Z0-9]{10})\)".*/\1/')"
if [[ -z "$signing_identity" || ! "$signing_team" =~ ^[A-Z0-9]{10}$ ]]; then
  echo "Could not resolve the installed Developer ID identity and Team ID." >&2
  exit 2
fi

run_root="$(mktemp -d "$scratch/run.XXXXXX")"
app="$run_root/NativeAotHost.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks"
swift_module_cache="$scratch/swift-module-cache"
mkdir -p "$swift_module_cache"
cp "$script_dir/Host/Info.plist" "$app/Contents/Info.plist"
cp "$native_library" "$app/Contents/Frameworks/libProbe.dylib"

codesign --force --sign "$signing_identity" --timestamp=none \
  "$app/Contents/Frameworks/libProbe.dylib"
codesign --verify --strict "$app/Contents/Frameworks/libProbe.dylib"
library_signing="$(codesign -d --verbose=4 "$app/Contents/Frameworks/libProbe.dylib" 2>&1)"
library_team="$(printf '%s\n' "$library_signing" |
  awk -F= '/^TeamIdentifier=/ { print $2; exit }')"
if [[ "$library_team" != "$signing_team" ]]; then
  echo "NativeAOT dylib Team ID does not match the selected Developer ID." >&2
  exit 1
fi

xcrun swiftc -module-cache-path "$swift_module_cache" \
  "$script_dir/Host/NativeAotHost.swift" \
  -o "$app/Contents/MacOS/NativeAotHost"
codesign --force --sign "$signing_identity" --options runtime --timestamp=none "$app"
codesign --verify --deep --strict "$app"
app_signing="$(codesign -d --verbose=4 "$app" 2>&1)"
app_team="$(printf '%s\n' "$app_signing" |
  awk -F= '/^TeamIdentifier=/ { print $2; exit }')"
if [[ "$app_team" != "$signing_team" ]] ||
  ! printf '%s\n' "$app_signing" | grep -F 'flags=' | grep -F 'runtime' >/dev/null; then
  echo "Host signature lacks the expected Team ID or Hardened Runtime." >&2
  exit 1
fi
entitlements="$(codesign -d --entitlements :- "$app" 2>&1 || true)"
if printf '%s\n' "$entitlements" |
  grep -E 'com\.apple\.security\.cs\.(allow-jit|disable-library-validation)' >/dev/null; then
  echo "Host unexpectedly enables JIT or disables library validation." >&2
  exit 1
fi
echo "PASS strict signature verification for the dylib and app bundle"
echo "PASS Developer ID Team ID matches on host and NativeAOT dylib: $signing_team"
echo "PASS Hardened Runtime enabled; JIT and library-validation override entitlements absent"

file "$app/Contents/Frameworks/libProbe.dylib"
nm -gU "$app/Contents/Frameworks/libProbe.dylib" | tee "$run_root/exports.txt"
if ! grep -F '_netlift_nativeaot_probe' "$run_root/exports.txt" >/dev/null; then
  echo "Expected C entry point is missing from the NativeAOT dylib." >&2
  exit 1
fi
otool -L "$app/Contents/Frameworks/libProbe.dylib" |
  tee "$run_root/module-dependencies.txt"
if grep -Ei 'lib(coreclr|clrjit)|/dotnet/|\.nuget' \
  "$run_root/module-dependencies.txt" >/dev/null; then
  echo "NativeAOT dylib retains a CoreCLR/JIT or .NET installation dependency." >&2
  exit 1
fi

positive_log="$run_root/positive-host.log"
if ! "$app/Contents/MacOS/NativeAotHost" >"$positive_log" 2>&1; then
  cat "$positive_log" >&2
  echo "RESULT=HOST_FAILED"
  exit 1
fi
cat "$positive_log"
if ! grep -F 'MANAGED_ENTRY_RESULT=0x4e4c' "$positive_log" >/dev/null ||
  ! grep -F 'PASS deterministic NativeAOT entry and no CoreCLR/JIT image' \
    "$positive_log" >/dev/null; then
  echo "Host did not verify the expected result and loaded-image policy." >&2
  exit 1
fi
if ! grep -F "IMAGE=$app/Contents/Frameworks/libProbe.dylib" \
  "$positive_log" >/dev/null; then
  echo "Host did not load the app-bundled NativeAOT dylib." >&2
  exit 1
fi
image_count="$(grep -c '^IMAGE=' "$positive_log")"
echo "PASS loaded app-bundled dylib; dyld image snapshot count=$image_count"

tampered_app="$run_root/TamperedHost.app"
ditto "$app" "$tampered_app"
printf '\nNETLIFT-TAMPER\n' >>"$tampered_app/Contents/Frameworks/libProbe.dylib"
if codesign --verify --deep --strict "$tampered_app" \
  >"$run_root/tamper-check.log" 2>&1; then
  echo "Tampered app bundle unexpectedly passed strict deep signature verification." >&2
  exit 1
fi
cat "$run_root/tamper-check.log"
echo "PASS tampered NativeAOT asset fails strict app signature verification"
echo "RUN_ARTIFACTS=$run_root"
echo "RESULT=PASS_NATIVEAOT_DYLIB_DEVELOPER_ID_HARDENED_RUNTIME"
