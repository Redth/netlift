#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import re
import zipfile
from pathlib import Path


PRODUCT = Path(__file__).resolve().parents[1]
MAVEN_LIBRARY_PATTERN = re.compile(
    r'maven_library \{\s+groupId: "([^"]+)"\s+'
    r'artifactId: "([^"]+)"\s+version: "([^"]+)"',
    re.MULTILINE,
)
REQUIRED_APP_SYMBOL_LIBRARIES = {
    "libassembly-store.so.dbg",
    "libmonodroid.so.dbg",
}
REQUIRED_RUNTIME_SYMBOL_LIBRARIES = {
    "libnetlift.so",
    "libnetlift_android.so",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sdk-dependencies", type=Path, required=True)
    parser.add_argument("--symbols", type=Path, required=True)
    parser.add_argument("--runtime-symbols", type=Path, required=True)
    parser.add_argument("--runtime-pom", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    for path in (
        args.sdk_dependencies,
        args.symbols,
        args.runtime_symbols,
        args.runtime_pom,
    ):
        if not path.is_file():
            raise RuntimeError(f"Dependency audit input does not exist: {path}")

    inventory = json.loads(
        (PRODUCT / "dependency-licenses.json").read_text(encoding="utf-8")
    )
    if inventory["schemaVersion"] != 1:
        raise RuntimeError("Unsupported dependency-license schema.")
    expected = {
        dependency["id"]: dependency
        for dependency in inventory["dependencies"]
    }
    dependency_text = args.sdk_dependencies.read_text(encoding="utf-8")
    actual = {
        f"{group}:{artifact}": version
        for group, artifact, version in MAVEN_LIBRARY_PATTERN.findall(
            dependency_text
        )
    }
    if set(actual) != set(expected):
        missing = sorted(set(expected).difference(actual))
        unexpected = sorted(set(actual).difference(expected))
        raise RuntimeError(
            f"Dependency inventory mismatch; missing={missing}, unexpected={unexpected}"
        )
    for dependency_id, version in actual.items():
        if expected[dependency_id]["version"] != version:
            raise RuntimeError(
                f"Dependency version mismatch for {dependency_id}: {version}"
            )
        if not expected[dependency_id]["license"]:
            raise RuntimeError(f"Dependency license is missing: {dependency_id}")

    pom = args.runtime_pom.read_text(encoding="utf-8")
    if "<name>MIT</name>" not in pom:
        raise RuntimeError("Android runtime POM does not declare the MIT license.")
    if "<version>0.1.0-alpha.3</version>" not in pom:
        raise RuntimeError("Android runtime POM has an unexpected version.")

    with zipfile.ZipFile(args.symbols) as symbols:
        app_symbol_libraries = {Path(name).name for name in symbols.namelist()}
        for name in symbols.namelist():
            if b".symtab" not in symbols.read(name):
                raise RuntimeError(f"App symbol has no native symbol table: {name}")
    missing_app_symbols = sorted(
        REQUIRED_APP_SYMBOL_LIBRARIES.difference(app_symbol_libraries)
    )
    if missing_app_symbols:
        raise RuntimeError(
            "App debug-symbol archive is incomplete: "
            + ", ".join(missing_app_symbols)
        )
    with zipfile.ZipFile(args.runtime_symbols) as symbols:
        runtime_symbol_libraries = {Path(name).name for name in symbols.namelist()}
        for name in symbols.namelist():
            if b".debug_info" not in symbols.read(name):
                raise RuntimeError(f"Runtime symbol has no DWARF debug info: {name}")
    missing_runtime_symbols = sorted(
        REQUIRED_RUNTIME_SYMBOL_LIBRARIES.difference(runtime_symbol_libraries)
    )
    if missing_runtime_symbols:
        raise RuntimeError(
            "Runtime debug-symbol archive is incomplete: "
            + ", ".join(missing_runtime_symbols)
        )

    for required in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        if not (PRODUCT / required).is_file():
            raise RuntimeError(f"Product license file is missing: {required}")
    if not inventory["workloadComponents"]:
        raise RuntimeError("Workload component licenses are not declared.")

    print("NetLift dependency and symbol closure audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
