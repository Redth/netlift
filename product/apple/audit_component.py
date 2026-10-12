#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import plistlib
import subprocess
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("app", type=Path)
    args = parser.parse_args()
    app = args.app.resolve()
    manifest_path = app / "netlift-apple-component.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (
        manifest["platform"] != "ios"
        or manifest["environment"] != "simulator"
        or manifest["architectures"] != ["arm64"]
    ):
        raise RuntimeError("Apple component claims an unvalidated target")
    if manifest["runtimeIdentifier"] != "iossimulator-arm64":
        raise RuntimeError("Apple component has an unexpected runtime identifier")
    if manifest["platformObjectProjection"] != {
        "enabled": True,
        "adapter": "netlift.apple.uiview",
        "ownership": ["borrowed", "retained"],
        "threadAffinity": "main",
    }:
        raise RuntimeError("Apple component platform-object metadata is invalid")

    for item in manifest["files"]:
        path = app / item["path"]
        if not path.is_file():
            raise RuntimeError(f"Apple component file is missing: {item['path']}")
        if sha256(path) != item["sha256"]:
            raise RuntimeError(f"Apple component hash mismatch: {item['path']}")

    with (app / "Info.plist").open("rb") as stream:
        info = plistlib.load(stream)
    executable = app / info["CFBundleExecutable"]
    symbols = subprocess.run(
        ["xcrun", "nm", str(executable)],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout
    for symbol in (
        "_main",
        "_netlift_get_api",
        "_netlift_native_ui_main",
        "_netlift_workload_apple_start",
    ):
        if symbol not in symbols:
            raise RuntimeError(f"Apple component executable is missing {symbol}")

    generic_runtime = (
        Path(__file__).resolve().parent
        / "netlift-swift"
        / "artifacts"
        / "NetLiftRuntime.xcframework"
    )
    generic_files = [path.name for path in generic_runtime.rglob("*") if path.is_file()]
    forbidden = {
        "Orders.Contracts.dll",
        "NetLift.Generated.Apple.dll",
        "NetLift.Runtime.dll",
        "NetLift.ApplePlatform.dll",
    }
    leaked = forbidden.intersection(generic_files)
    if leaked:
        raise RuntimeError(
            "Generic Apple runtime contains app-specific managed files: "
            + ", ".join(sorted(leaked))
        )
    print(f"Apple component audit passed: {app}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
