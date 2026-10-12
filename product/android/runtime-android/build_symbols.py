#!/usr/bin/env python3

from __future__ import annotations

import argparse
import zipfile
from pathlib import Path


FIXED_ZIP_TIME = (2020, 1, 1, 0, 0, 0)
RUNTIME_ROOT = Path(__file__).resolve().parent
REQUIRED_LIBRARIES = (
    "libnetlift.so",
    "libnetlift_android.so",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    cxx_root = RUNTIME_ROOT / "build" / "intermediates" / "cxx" / "RelWithDebInfo"
    libraries: list[Path] = []
    for name in REQUIRED_LIBRARIES:
        candidates = sorted(cxx_root.glob(f"*/obj/arm64-v8a/{name}"))
        if len(candidates) != 1:
            raise RuntimeError(
                f"Expected one unstripped {name} under {cxx_root}, "
                f"found {len(candidates)}."
            )
        libraries.append(candidates[0])

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(
        output,
        "w",
        compression=zipfile.ZIP_DEFLATED,
    ) as archive:
        for library in libraries:
            info = zipfile.ZipInfo(
                f"arm64-v8a/{library.name}",
                FIXED_ZIP_TIME,
            )
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, library.read_bytes())
    print(f"Built Android runtime symbols: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
