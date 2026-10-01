#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export DEVELOPER_DIR=${DEVELOPER_DIR:-/Applications/Xcode_26.6.app/Contents/Developer}
artifact_root=${NETLIFT_V5_IOS_ARTIFACTS:-"$script_dir/.artifacts"}
mkdir -p "$artifact_root"
artifact_root=$(CDPATH= cd -- "$artifact_root" && pwd)
export NETLIFT_V5_IOS_ARTIFACTS="$artifact_root"
derived_data=${DERIVED_DATA_PATH:-"$artifact_root/DerivedData"}
simulator_name="NetLiftV5Proof-$(date +%Y%m%d%H%M%S)"
if [ "${NETLIFT_V5_IOS_ASYNC_EXECUTION:-0}" = "1" ]; then
    async_direction=${NETLIFT_V5_IOS_ASYNC_DIRECTION:-old}
    case "$async_direction" in
        old|new) ;;
        *)
            echo "NETLIFT_V5_IOS_ASYNC_DIRECTION must be old or new." >&2
            exit 2
            ;;
    esac
    simulator_name="NetLiftAsyncExecution-${async_direction}-$(date +%Y%m%d%H%M%S)"
elif [ "${NETLIFT_V5_IOS_ASYNC_RESULTS:-0}" = "1" ]; then
    simulator_name="NetLiftAsyncResultProof-$(date +%Y%m%d%H%M%S)"
fi
bundle_id=org.netlift.nativev5swifthost

sh "$script_dir/prepare.sh"
project_spec="$script_dir/.NativeV5SwiftHost.$$.project.yml"
trap 'rm -f "$project_spec"' EXIT HUP INT TERM
project_root="$artifact_root/xcode-project"
mkdir -p "$project_root"
python3 - "$script_dir/project.yml" "$project_spec" "$artifact_root/swift-package" "$script_dir" <<'PY'
import json
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text(encoding="utf-8")
package_path = json.dumps(sys.argv[3])
script_dir = Path(sys.argv[4])
source = source.replace("    path: .artifacts/swift-package",
                        f"    path: {package_path}")
if "path: .artifacts/swift-package" in source:
    raise SystemExit("Could not substitute the Swift package artifact path.")
source = source.replace("- NativeV5SwiftHostApp.swift",
                        f"- {json.dumps(str(script_dir / 'NativeV5SwiftHostApp.swift'))}")
source = source.replace("INFOPLIST_FILE: Info.plist",
                        f"INFOPLIST_FILE: {json.dumps(str(script_dir / 'Info.plist'))}")
source = source.replace('sh "$SRCROOT/stage-runtime.sh"',
                        f"sh {json.dumps(str(script_dir / 'stage-runtime.sh'))}")
Path(sys.argv[2]).write_text(source, encoding="utf-8")
PY
xcodegen generate --spec "$project_spec" --project "$project_root"
if [ "${NETLIFT_V5_IOS_ASYNC_EXECUTION:-0}" = "1" ]; then
    swift_conditions="NETLIFT_V5_ASYNC_PROTOCOL NETLIFT_V5_ASYNC_RESULTS NETLIFT_V5_ASYNC_EXECUTION"
    if [ "$async_direction" = "new" ]; then
        swift_conditions="$swift_conditions NETLIFT_V5_ASYNC_NEW"
    fi
    xcodebuild -quiet \
        -project "$project_root/NativeV5SwiftHost.xcodeproj" \
        -scheme NativeV5SwiftHost \
        -configuration Debug \
        -sdk iphonesimulator \
        -destination 'generic/platform=iOS Simulator' \
        -derivedDataPath "$derived_data" \
        CODE_SIGNING_ALLOWED=YES \
        CODE_SIGN_IDENTITY=- \
        SWIFT_ACTIVE_COMPILATION_CONDITIONS="$swift_conditions" \
        clean build
elif [ "${NETLIFT_V5_IOS_ASYNC_RESULTS:-0}" = "1" ]; then
    xcodebuild -quiet \
        -project "$project_root/NativeV5SwiftHost.xcodeproj" \
        -scheme NativeV5SwiftHost \
        -configuration Debug \
        -sdk iphonesimulator \
        -destination 'generic/platform=iOS Simulator' \
        -derivedDataPath "$derived_data" \
        CODE_SIGNING_ALLOWED=YES \
        CODE_SIGN_IDENTITY=- \
        SWIFT_ACTIVE_COMPILATION_CONDITIONS="NETLIFT_V5_ASYNC_PROTOCOL NETLIFT_V5_ASYNC_RESULTS" \
        clean build
else
    xcodebuild -quiet \
        -project "$project_root/NativeV5SwiftHost.xcodeproj" \
        -scheme NativeV5SwiftHost \
        -configuration Debug \
        -sdk iphonesimulator \
        -destination 'generic/platform=iOS Simulator' \
        -derivedDataPath "$derived_data" \
        CODE_SIGNING_ALLOWED=YES \
        CODE_SIGN_IDENTITY=- \
        clean build
fi

requested_simulator=${NETLIFT_V5_IOS_SIMULATOR_UDID:-}
simulator_info=$(python3 - "$DEVELOPER_DIR" "$requested_simulator" <<'PY'
import json
import os
import subprocess
import sys

developer_dir = sys.argv[1]
requested_udid = sys.argv[2]
def simctl(*args):
    return subprocess.check_output(
        ["xcrun", "simctl", *args],
        env=os.environ | {"DEVELOPER_DIR": developer_dir},
        text=True)

runtimes = json.loads(simctl("list", "-j", "runtimes"))["runtimes"]
devices = json.loads(simctl("list", "-j", "devicetypes"))["devicetypes"]
if requested_udid:
    all_devices = json.loads(simctl("list", "-j", "devices"))["devices"]
    for runtime_id, runtime_devices in all_devices.items():
        for device in runtime_devices:
            if device.get("udid") == requested_udid:
                if (not runtime_id.endswith("iOS-26-5") or
                        device.get("deviceTypeIdentifier") !=
                        "com.apple.CoreSimulator.SimDeviceType.iPhone-11-Pro"):
                    raise SystemExit("Requested simulator must be an iPhone 11 Pro running iOS 26.5.")
                print(device["name"] + "\t" + device["state"] + "\t" + requested_udid)
                raise SystemExit(0)
    raise SystemExit("Requested UDID is not an available iOS Simulator device.")

runtime = next((item for item in runtimes
                if item.get("name") == "iOS 26.5" and item.get("isAvailable")), None)
device = next((item for item in devices
               if item.get("name") == "iPhone 11 Pro"), None)
if runtime is None or device is None:
    raise SystemExit("Required iOS 26.5 runtime or iPhone 11 Pro simulator type is unavailable.")
print(runtime["identifier"] + "\t" + device["identifier"])
PY
)
if [ -n "$requested_simulator" ]; then
    simulator_name=$(printf '%s\n' "$simulator_info" | cut -f 1)
    simulator_state=$(printf '%s\n' "$simulator_info" | cut -f 2)
    simulator_udid=$(printf '%s\n' "$simulator_info" | cut -f 3)
    if [ "$simulator_state" = "Shutdown" ]; then
        xcrun simctl boot "$simulator_udid"
    elif [ "$simulator_state" != "Booted" ]; then
        echo "Requested simulator is not in a bootable state: $simulator_state" >&2
        exit 1
    fi
else
    runtime_id=$(printf '%s\n' "$simulator_info" | cut -f 1)
    device_type=$(printf '%s\n' "$simulator_info" | cut -f 2)
    simulator_udid=$(xcrun simctl create "$simulator_name" "$device_type" "$runtime_id")
    xcrun simctl boot "$simulator_udid"
fi
printf '%s\n' "$simulator_udid" > "$artifact_root/dedicated-simulator.udid"
xcrun simctl bootstatus "$simulator_udid" -b
app="$derived_data/Build/Products/Debug-iphonesimulator/NativeV5SwiftHost.app"
xcrun simctl install "$simulator_udid" "$app"
xcrun simctl launch "$simulator_udid" "$bundle_id"
if [ "${NETLIFT_V5_IOS_ASYNC_EXECUTION:-0}" = "1" ]; then
    if [ "$async_direction" = "old" ]; then
        expected_pass="ASYNC-EXEC PASS native-new/managed-old"
    else
        expected_pass="ASYNC-EXEC PASS native-old/managed-new"
    fi
    container_path=$(xcrun simctl get_app_container "$simulator_udid" "$bundle_id" data)
    proof_file="$container_path/Documents/async-execution-proof.log"
    proof_captured=0
    attempt=0
    while [ "$attempt" -lt 45 ]; do
        if [ -f "$proof_file" ] && grep -Fq "$expected_pass" "$proof_file"; then
            proof_captured=1
            break
        fi
        if [ -f "$proof_file" ] && grep -Fq "ASYNC-EXEC FAIL" "$proof_file"; then
            break
        fi
        attempt=$((attempt + 1))
        sleep 1
    done
    if [ "$proof_captured" -ne 1 ]; then
        echo "Simulator AsyncCallbackV1 acceptance did not reach $expected_pass." >&2
        if [ -f "$proof_file" ]; then
            tail -n 80 "$proof_file" >&2
        fi
        exit 1
    fi
    simulator_screenshot="$artifact_root/async-execution-simulator.png"
    xcrun simctl io "$simulator_udid" screenshot "$simulator_screenshot" >/dev/null
    proof_artifact="$artifact_root/async-execution-proof.log"
    cp "$proof_file" "$proof_artifact"
    printf 'Simulator acceptance artifact: %s\n' "$proof_artifact"
    grep -F "$expected_pass" "$proof_file"
    printf 'Simulator screenshot: %s\n' "$simulator_screenshot"
fi
printf 'Dedicated simulator: %s (%s, iOS 26.5)\n' "$simulator_name" "$simulator_udid"
printf 'App bundle: %s\n' "$app"
