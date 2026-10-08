#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import os
import plistlib
import subprocess
from pathlib import Path


PROOF_ROOT = Path(__file__).resolve().parents[2]
APP_NAME = "NetLift.WorkloadLibraryMode.Apple"
DEFAULT_APP = (
    PROOF_ROOT
    / ".artifacts"
    / "apple-native-app"
    / f"{APP_NAME}.app"
)
COMPONENT_MANIFEST = "netlift-workload-component.json"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def output(*args: str) -> str:
    environment = os.environ.copy()
    environment["DEVELOPER_DIR"] = "/Applications/Xcode_26.6.app/Contents/Developer"
    return subprocess.run(
        args,
        check=True,
        env=environment,
        text=True,
        stdout=subprocess.PIPE,
    ).stdout


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", type=Path, default=DEFAULT_APP)
    args = parser.parse_args()

    if not args.app.is_dir():
        raise RuntimeError(f"Apple app does not exist: {args.app}")

    manifest_path = args.app / COMPONENT_MANIFEST
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest["platform"] != "apple" or manifest["runtimeFlavor"] != "CoreCLR":
        raise RuntimeError("Apple component manifest identifies the wrong platform")

    for file in manifest["files"]:
        path = args.app / file["path"]
        if not path.is_file():
            raise RuntimeError(f"Apple component payload is missing: {file['path']}")
        if digest(path) != file["sha256"]:
            raise RuntimeError(f"Apple component hash mismatch: {file['path']}")

    with (args.app / "Info.plist").open("rb") as stream:
        plist = plistlib.load(stream)
    if plist.get("CFBundleIdentifier") != "dev.netlift.workloadlibrarymode.nativeapp":
        raise RuntimeError("Apple app has an unexpected bundle identifier")

    executable = args.app / APP_NAME
    symbols = output("xcrun", "nm", str(executable))
    required_symbols = {
        "_main",
        "_netlift_native_ui_main",
        "_netlift_workload_apple_start",
        "_xamarin_main",
        "_OBJC_CLASS_$_NetLiftManagedProofBridge",
        "_OBJC_CLASS_$_NetLiftNativeAppDelegate",
    }
    missing_symbols = sorted(
        symbol for symbol in required_symbols if symbol not in symbols
    )
    if missing_symbols:
        raise RuntimeError(
            f"Apple executable is missing symbols: {', '.join(missing_symbols)}"
        )

    libraries = output("xcrun", "otool", "-L", str(executable))
    required_libraries = {
        "@executable_path/libcoreclr.dylib",
        "@executable_path/libxamarin-dotnet-coreclr-debug.dylib",
    }
    missing_libraries = sorted(
        library for library in required_libraries if library not in libraries
    )
    if missing_libraries:
        raise RuntimeError(
            f"Apple executable is missing runtime links: {', '.join(missing_libraries)}"
        )

    print(f"Apple package audit passed: {args.app}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
