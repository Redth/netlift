#!/usr/bin/env python3

from __future__ import annotations

import json
import shutil
import subprocess
from pathlib import Path


APPLE_ROOT = Path(__file__).resolve().parent
PROJECT = APPLE_ROOT / "src" / "NetLift.SwiftDriver" / "NetLift.SwiftDriver.csproj"
ARTIFACTS = APPLE_ROOT / "netlift-swift" / "artifacts"
BUNDLE = ARTIFACTS / "netlift-swift-tools.artifactbundle"
VERSION = "0.1.0-alpha.10"


def main() -> int:
    publish = ARTIFACTS / "tool-publish"
    if publish.exists():
        shutil.rmtree(publish)
    subprocess.run(
        [
            "dotnet",
            "publish",
            str(PROJECT),
            "-c",
            "Release",
            "-r",
            "osx-arm64",
            "--self-contained",
            "true",
            "-o",
            str(publish),
            "-v:minimal",
        ],
        check=True,
    )
    if BUNDLE.exists():
        shutil.rmtree(BUNDLE)
    executable_root = BUNDLE / f"netlift-swift-{VERSION}-macos" / "bin"
    executable_root.mkdir(parents=True)
    shutil.copy2(publish / "netlift-swift", executable_root / "netlift-swift")
    (BUNDLE / "info.json").write_text(
        json.dumps(
            {
                "schemaVersion": "1.0",
                "artifacts": {
                    "netlift-swift": {
                        "version": VERSION,
                        "type": "executable",
                        "variants": [
                            {
                                "path": (
                                    f"netlift-swift-{VERSION}-macos/"
                                    "bin/netlift-swift"
                                ),
                                "supportedTriples": ["arm64-apple-macosx"],
                            }
                        ],
                    }
                },
            },
            indent=2,
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    print(f"Built SwiftPM executable artifact bundle: {BUNDLE}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
