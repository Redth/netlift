#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
import plistlib
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parent
ARTIFACTS = ROOT / "artifacts"
FRAMEWORK = (
    ARTIFACTS
    / "NetLiftRuntime.xcframework"
    / "ios-arm64-simulator"
    / "NetLiftC.framework"
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    manifest = json.loads(
        (ARTIFACTS / "netlift-runtime-apple.json").read_text(encoding="utf-8")
    )
    if manifest["abiVersion"] != 1:
        raise RuntimeError("Apple runtime declares an unexpected ABI")
    supported = [
        target
        for target in manifest["supportedTargets"]
        if target["status"] == "supported-preview"
    ]
    if supported != [
        {
            "platform": "ios",
            "environment": "simulator",
            "architectures": ["arm64"],
            "minimumOSVersion": "15.0",
            "status": "supported-preview",
        }
    ]:
        raise RuntimeError("Apple runtime support matrix is broader than validated")

    binary = FRAMEWORK / "NetLiftC"
    header = FRAMEWORK / "Headers" / "netlift.h"
    if sha256(binary) != manifest["files"]["NetLiftC"]:
        raise RuntimeError("Apple runtime binary hash mismatch")
    if sha256(header) != manifest["files"]["netlift.h"]:
        raise RuntimeError("Apple runtime header hash mismatch")
    with (FRAMEWORK / "Info.plist").open("rb") as stream:
        info = plistlib.load(stream)
    if info["CFBundleSupportedPlatforms"] != ["iPhoneSimulator"]:
        raise RuntimeError("Apple runtime framework claims an unvalidated platform")
    symbols = subprocess.run(
        ["xcrun", "nm", "-gU", str(binary)],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout
    if "_netlift_get_api" not in symbols:
        raise RuntimeError("Apple runtime framework is missing netlift_get_api")
    if "Orders" in symbols:
        raise RuntimeError("Apple runtime framework contains sample symbols")
    print(f"Apple runtime audit passed: {FRAMEWORK}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
