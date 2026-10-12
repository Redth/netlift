#!/usr/bin/env python3

from __future__ import annotations

import argparse
import re
import subprocess
import time
from pathlib import Path


PACKAGE = "dev.netlift.workloadlibrarymode.app"
REQUIRED_MARKERS = {
    "NETLIFT_ANDROID_VIEW_PASSED",
    "NETLIFT_ANDROID_CROSS_VERSION_PASSED",
    "NETLIFT_ANDROID_RESULT_ERROR_PASSED",
    "NETLIFT_ANDROID_UUID_PASSED",
    "NETLIFT_ANDROID_ADAPTERS_PASSED",
    "NETLIFT_ANDROID_MALFORMED_ADAPTERS_PASSED",
    "NETLIFT_ANDROID_COLLECTIONS_PASSED",
    "NETLIFT_ANDROID_STREAM_PASSED",
    "NETLIFT_ANDROID_CALLBACK_PASSED",
    "NETLIFT_ANDROID_CANCELLATION_PASSED",
}


def run(*args: str, capture: bool = True) -> str:
    result = subprocess.run(
        args,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.STDOUT if capture else None,
    )
    output = result.stdout if capture else ""
    if result.returncode != 0:
        raise RuntimeError(
            f"Command failed ({result.returncode}): {' '.join(args)}\n{output}"
        )
    return output


def adb(adb_path: Path, *args: str) -> str:
    return run(str(adb_path), *args)


def wait_for_acceptance(adb_path: Path, timeout_seconds: int = 120) -> None:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        logs = adb(
            adb_path,
            "logcat",
            "-d",
            "-s",
            "NetLiftAcceptance:I",
            "*:S",
        )
        if "NETLIFT_ANDROID_ACCEPTANCE_FAILED" in logs:
            raise RuntimeError(f"Android release acceptance failed:\n{logs}")
        missing = sorted(marker for marker in REQUIRED_MARKERS if marker not in logs)
        if not missing:
            return
        time.sleep(2)
    raise RuntimeError(
        "Android release acceptance timed out; missing: " + ", ".join(missing)
    )


def cold_launch(adb_path: Path) -> None:
    adb(adb_path, "shell", "am", "force-stop", PACKAGE)
    adb(adb_path, "logcat", "-c")
    adb(
        adb_path,
        "shell",
        "am",
        "start",
        "-W",
        "-n",
        f"{PACKAGE}/.MainActivity",
    )
    wait_for_acceptance(adb_path)


def require_version(adb_path: Path, expected: int) -> None:
    package = adb(adb_path, "shell", "dumpsys", "package", PACKAGE)
    match = re.search(r"versionCode=(\d+)", package)
    if match is None or int(match.group(1)) != expected:
        raise RuntimeError(f"Expected installed versionCode {expected}.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", type=Path, required=True)
    parser.add_argument("--bundletool", type=Path, required=True)
    parser.add_argument("--initial-apks", type=Path, required=True)
    parser.add_argument("--update-apk", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    for path in (
        args.adb,
        args.bundletool,
        args.initial_apks,
        args.update_apk,
    ):
        if not path.is_file():
            raise RuntimeError(f"Release acceptance input does not exist: {path}")

    devices = [
        line
        for line in adb(args.adb, "devices").splitlines()[1:]
        if line.endswith("\tdevice")
    ]
    if len(devices) != 1:
        raise RuntimeError(f"Expected one connected Android device, found {len(devices)}.")

    try:
        subprocess.run(
            [str(args.adb), "uninstall", PACKAGE],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        run(
            "java",
            "-jar",
            str(args.bundletool),
            "install-apks",
            f"--apks={args.initial_apks}",
            f"--adb={args.adb}",
        )
        require_version(args.adb, 1)
        adb(args.adb, "shell", "cmd", "connectivity", "airplane-mode", "enable")
        cold_launch(args.adb)
        cold_launch(args.adb)

        adb(args.adb, "install", "-r", str(args.update_apk))
        require_version(args.adb, 2)
        cold_launch(args.adb)
    finally:
        adb(args.adb, "shell", "cmd", "connectivity", "airplane-mode", "disable")

    print("Android signed split, offline, relaunch, and update acceptance passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
