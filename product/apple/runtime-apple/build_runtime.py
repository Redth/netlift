#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
import os
import plistlib
import shutil
import subprocess
from pathlib import Path


PRODUCT = Path(__file__).resolve().parents[2]
SOURCE = PRODUCT / "src" / "NetLift.NativeCore" / "netlift.c"
HEADER = PRODUCT / "include" / "netlift.h"
ARTIFACTS = PRODUCT / "apple" / "netlift-swift" / "artifacts"
OUTPUT = ARTIFACTS / "runtime-build"
XCODE = Path("/Applications/Xcode_26.6.app/Contents/Developer")
DEPLOYMENT_TARGET = "15.0"


def run(*args: str) -> None:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = str(XCODE)
    subprocess.run(args, check=True, env=environment)


def output(*args: str) -> str:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = str(XCODE)
    return subprocess.run(
        args,
        check=True,
        env=environment,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout.strip()


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    if not XCODE.is_dir():
        raise RuntimeError(f"Compatible Xcode was not found: {XCODE}")
    if OUTPUT.exists():
        shutil.rmtree(OUTPUT)
    xcframework = ARTIFACTS / "NetLiftRuntime.xcframework"
    if xcframework.exists():
        shutil.rmtree(xcframework)
    framework = OUTPUT / "staging" / "NetLiftC.framework"
    headers = framework / "Headers"
    modules = framework / "Modules"
    headers.mkdir(parents=True)
    modules.mkdir()

    sdk = output("xcrun", "--sdk", "iphonesimulator", "--show-sdk-path")
    object_file = OUTPUT / "staging" / "netlift.o"
    run(
        "xcrun",
        "clang",
        "-target",
        f"arm64-apple-ios{DEPLOYMENT_TARGET}-simulator",
        "-isysroot",
        sdk,
        "-std=c11",
        "-fvisibility=hidden",
        f"-I{HEADER.parent}",
        "-c",
        str(SOURCE),
        "-o",
        str(object_file),
    )
    run(
        "xcrun",
        "libtool",
        "-static",
        "-o",
        str(framework / "NetLiftC"),
        str(object_file),
    )
    shutil.copy2(HEADER, headers / HEADER.name)
    (modules / "module.modulemap").write_text(
        'framework module NetLiftC {\n'
        '  umbrella header "netlift.h"\n'
        "  export *\n"
        "}\n",
        encoding="utf-8",
    )
    with (framework / "Info.plist").open("wb") as stream:
        plistlib.dump(
            {
                "CFBundleDevelopmentRegion": "en",
                "CFBundleExecutable": "NetLiftC",
                "CFBundleIdentifier": "dev.netlift.runtime.c",
                "CFBundleInfoDictionaryVersion": "6.0",
                "CFBundleName": "NetLiftC",
                "CFBundlePackageType": "FMWK",
                "CFBundleShortVersionString": "0.1.0",
                "CFBundleSupportedPlatforms": ["iPhoneSimulator"],
                "CFBundleVersion": "1",
                "MinimumOSVersion": DEPLOYMENT_TARGET,
            },
            stream,
            fmt=plistlib.FMT_XML,
            sort_keys=True,
        )

    run(
        "xcrun",
        "xcodebuild",
        "-create-xcframework",
        "-framework",
        str(framework),
        "-output",
        str(xcframework),
    )
    manifest = {
        "schemaVersion": 1,
        "packageVersion": "0.1.0-alpha.5",
        "abiVersion": 2,
        "supportedTargets": [
            {
                "platform": "ios",
                "environment": "simulator",
                "architectures": ["arm64"],
                "minimumOSVersion": DEPLOYMENT_TARGET,
                "status": "supported-preview",
            },
            {
                "platform": "ios",
                "environment": "device",
                "architectures": ["arm64"],
                "status": "unsupported-until-device-runtime-proof",
            },
        ],
        "files": {
            "NetLiftC": sha256(
                xcframework
                / "ios-arm64-simulator"
                / "NetLiftC.framework"
                / "NetLiftC"
            ),
            "netlift.h": sha256(
                xcframework
                / "ios-arm64-simulator"
                / "NetLiftC.framework"
                / "Headers"
                / "netlift.h"
            ),
        },
    }
    (ARTIFACTS / "netlift-runtime-apple.json").write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(f"Built Apple runtime XCFramework: {xcframework}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
