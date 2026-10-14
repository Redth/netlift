#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import zipfile
from pathlib import Path


RUNTIME_MANIFEST = "assets/netlift-runtime-android.json"
EXPECTED_NATIVE_LIBRARIES = {
    "jni/arm64-v8a/libnetlift.so",
    "jni/arm64-v8a/libnetlift_android.so",
}
REQUIRED_CLASSES = {
    "dev/netlift/runtime/BuildConfig.class",
    "dev/netlift/runtime/DotnetBootstrap.class",
    "dev/netlift/runtime/NetLiftRuntime.class",
    "dev/netlift/runtime/NetLiftRuntimeDiagnostics.class",
    "dev/netlift/runtime/NetLiftNative.class",
}
FORBIDDEN_TOKENS = (
    b"Orders",
    b"workloadlibrarymode",
    b"assembly-store",
    b"libcoreclr",
    b"libmonodroid",
    b"libxamarin-app",
    b".dll",
)


def require_unique_entries(archive: zipfile.ZipFile) -> None:
    names = [entry.filename for entry in archive.infolist()]
    duplicates = sorted({name for name in names if names.count(name) > 1})
    if duplicates:
        raise RuntimeError(f"AAR contains duplicate entries: {', '.join(duplicates)}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("aar", type=Path)
    args = parser.parse_args()
    if not args.aar.is_file():
        raise RuntimeError(f"Runtime AAR does not exist: {args.aar}")

    checksum_path = args.aar.with_suffix(args.aar.suffix + ".sha256")
    if checksum_path.is_file():
        expected = checksum_path.read_text(encoding="utf-8").split()[0]
        actual = hashlib.sha256(args.aar.read_bytes()).hexdigest()
        if actual != expected:
            raise RuntimeError("Published runtime AAR checksum does not match.")

    with zipfile.ZipFile(args.aar) as aar:
        require_unique_entries(aar)
        names = set(aar.namelist())
        native_libraries = {
            name for name in names if name.startswith("jni/") and name.endswith(".so")
        }
        if native_libraries != EXPECTED_NATIVE_LIBRARIES:
            raise RuntimeError(
                "Unexpected runtime native libraries: "
                + ", ".join(sorted(native_libraries))
            )
        if RUNTIME_MANIFEST not in names:
            raise RuntimeError("Runtime AAR is missing its compatibility manifest.")
        manifest = json.loads(aar.read(RUNTIME_MANIFEST))
        if manifest != {
            "schemaVersion": 1,
            "runtimeAbiVersion": 2,
            "minimumComponentSchemaVersion": 1,
        }:
            raise RuntimeError("Runtime AAR compatibility manifest is invalid.")
        if "provider" in aar.read("AndroidManifest.xml").decode("utf-8"):
            raise RuntimeError("Runtime AAR must not register an automatic provider.")

        with zipfile.ZipFile(aar.open("classes.jar")) as classes:
            class_names = set(classes.namelist())
            missing = sorted(REQUIRED_CLASSES.difference(class_names))
            if missing:
                raise RuntimeError(
                    f"Runtime AAR is missing classes: {', '.join(missing)}"
                )

        for entry in aar.infolist():
            if entry.is_dir():
                continue
            content = aar.read(entry)
            for token in FORBIDDEN_TOKENS:
                if token in entry.filename.encode() or token in content:
                    raise RuntimeError(
                        f"Runtime AAR contains forbidden app-specific token "
                        f"{token.decode()!r} in {entry.filename}."
                    )

    print(f"NetLift Android runtime AAR audit passed: {args.aar}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
