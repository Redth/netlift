#!/usr/bin/env python3

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import zipfile
from pathlib import Path


REQUIRED_NATIVE_LIBRARIES = {
    "lib/arm64-v8a/libassembly-store.so",
    "lib/arm64-v8a/libcoreclr.so",
    "lib/arm64-v8a/libmonodroid.so",
    "lib/arm64-v8a/libnetlift.so",
    "lib/arm64-v8a/libnetlift_android.so",
    "lib/arm64-v8a/libxamarin-app.so",
}
REQUIRED_BUNDLE_ENTRIES = {
    "base/assets/netlift-runtime-android.json",
    "base/assets/netlift-workload-component.json",
    "BUNDLE-METADATA/com.android.tools.build.libraries/dependencies.pb",
    "BUNDLE-METADATA/com.android.tools.build.obfuscation/proguard.map",
}
SIGNER_PATTERN = re.compile(r"certificate SHA-256 digest: ([0-9a-f]{64})")


def run(*args: str) -> str:
    result = subprocess.run(
        args,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    return result.stdout


def require_unique_entries(archive: zipfile.ZipFile) -> set[str]:
    names = [info.filename for info in archive.infolist()]
    duplicates = sorted({name for name in names if names.count(name) > 1})
    if duplicates:
        raise RuntimeError(f"Archive contains duplicate entries: {', '.join(duplicates)}")
    return set(names)


def verify_apk(apksigner: Path, apk: Path, expected_signer: str) -> None:
    output = run(str(apksigner), "verify", "--verbose", "--print-certs", str(apk))
    if "Verified using v2 scheme (APK Signature Scheme v2): true" not in output:
        raise RuntimeError(f"APK does not have a valid v2 signature: {apk}")
    match = SIGNER_PATTERN.search(output)
    if match is None or match.group(1) != expected_signer:
        raise RuntimeError(f"APK signer does not match the release signer: {apk}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--aab", type=Path, required=True)
    parser.add_argument("--apks", type=Path, required=True)
    parser.add_argument("--universal-apk", type=Path, required=True)
    parser.add_argument("--apksigner", type=Path, required=True)
    parser.add_argument("--expected-signer-sha256", required=True)
    parser.add_argument("--work-directory", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    for path in (args.aab, args.apks, args.universal_apk, args.apksigner):
        if not path.is_file():
            raise RuntimeError(f"Required delivery input does not exist: {path}")
    if not re.fullmatch(r"[0-9a-f]{64}", args.expected_signer_sha256):
        raise RuntimeError("Expected signer must be a lowercase SHA-256 digest.")

    signature = run("jarsigner", "-verify", str(args.aab))
    if "jar verified." not in signature:
        raise RuntimeError("App Bundle signature verification failed.")
    with zipfile.ZipFile(args.aab) as bundle:
        bundle_names = require_unique_entries(bundle)
    missing_bundle = sorted(REQUIRED_BUNDLE_ENTRIES.difference(bundle_names))
    if missing_bundle:
        raise RuntimeError(
            "App Bundle is missing release metadata: " + ", ".join(missing_bundle)
        )
    bundle_native = {
        name.replace("base/", "", 1)
        for name in bundle_names
        if name.startswith("base/lib/") and name.endswith(".so")
    }
    missing_native = sorted(REQUIRED_NATIVE_LIBRARIES.difference(bundle_native))
    if missing_native:
        raise RuntimeError(
            "App Bundle is missing native libraries: " + ", ".join(missing_native)
        )
    unexpected_abis = sorted(
        name
        for name in bundle_native
        if not name.startswith("lib/arm64-v8a/")
    )
    if unexpected_abis:
        raise RuntimeError(
            "App Bundle contains unadvertised ABI payloads: "
            + ", ".join(unexpected_abis)
        )

    work_directory = args.work_directory.resolve()
    if work_directory.exists():
        shutil.rmtree(work_directory)
    work_directory.mkdir(parents=True)
    with zipfile.ZipFile(args.apks) as archive:
        names = require_unique_entries(archive)
        split_names = sorted(
            name for name in names if name.startswith("splits/") and name.endswith(".apk")
        )
        if not any("arm64_v8a" in name for name in split_names):
            raise RuntimeError("Split set does not contain an ARM64 native split.")
        if not any("master" in name for name in split_names):
            raise RuntimeError("Split set does not contain a master split.")
        for name in split_names:
            destination = work_directory / Path(name).name
            destination.write_bytes(archive.read(name))
            verify_apk(args.apksigner, destination, args.expected_signer_sha256)

    verify_apk(
        args.apksigner,
        args.universal_apk,
        args.expected_signer_sha256,
    )
    with zipfile.ZipFile(args.universal_apk) as universal:
        universal_names = require_unique_entries(universal)
    missing_universal = sorted(REQUIRED_NATIVE_LIBRARIES.difference(universal_names))
    if missing_universal:
        raise RuntimeError(
            "Universal APK is missing native libraries: "
            + ", ".join(missing_universal)
        )
    for asset in (
        "assets/netlift-runtime-android.json",
        "assets/netlift-workload-component.json",
    ):
        if asset not in universal_names:
            raise RuntimeError(f"Universal APK is missing release asset: {asset}")

    shutil.rmtree(work_directory)
    print(f"Android delivery audit passed: {args.aab}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
