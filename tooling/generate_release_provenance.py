#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PRODUCT = ROOT
COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def directory_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    files = sorted(candidate for candidate in path.rglob("*") if candidate.is_file())
    for candidate in files:
        relative = candidate.relative_to(path).as_posix()
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(sha256(candidate).encode("ascii"))
        digest.update(b"\0")
    return digest.hexdigest()


def parse_mapping(values: list[str], option: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for value in values:
        key, separator, item = value.partition("=")
        if not separator or not key or not item:
            raise ValueError(f"{option} values must use name=value syntax.")
        if key in result:
            raise ValueError(f"{option} contains duplicate name {key!r}.")
        result[key] = item
    return result


def repository_path(value: str) -> tuple[str, Path]:
    path = Path(value)
    resolved = (ROOT / path).resolve() if not path.is_absolute() else path.resolve()
    try:
        relative = resolved.relative_to(ROOT).as_posix()
    except ValueError as error:
        raise ValueError(f"Artifact is outside the repository: {value}") from error
    return relative, resolved


def current_commit() -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-commit")
    parser.add_argument("--package-version", action="append", default=[])
    parser.add_argument("--artifact", action="append", default=[])
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    source_commit = args.source_commit or current_commit()
    if not COMMIT_PATTERN.fullmatch(source_commit):
        raise ValueError("Source commit must be a full lowercase Git commit hash.")

    package_versions = parse_mapping(args.package_version, "--package-version")
    artifact_values = parse_mapping(args.artifact, "--artifact")
    if not package_versions:
        raise ValueError("At least one package version is required.")
    if not artifact_values:
        raise ValueError("At least one artifact is required.")

    artifacts = []
    for artifact_id, value in sorted(artifact_values.items()):
        relative, path = repository_path(value)
        if path.is_file():
            kind = "file"
            digest = sha256(path)
        elif path.is_dir():
            kind = "directory"
            digest = directory_sha256(path)
        else:
            raise FileNotFoundError(f"Artifact does not exist: {path}")
        artifacts.append(
            {
                "id": artifact_id,
                "kind": kind,
                "path": relative,
                "sha256": digest,
            }
        )

    support_matrix_path = PRODUCT / "support-matrix.json"
    support_matrix = json.loads(support_matrix_path.read_text(encoding="utf-8"))
    release_gates_path = PRODUCT / "release-gates.json"
    provenance = {
        "schemaVersion": 1,
        "sourceCommit": source_commit,
        "supportMatrix": {
            "path": support_matrix_path.relative_to(ROOT).as_posix(),
            "sha256": sha256(support_matrix_path),
        },
        "releaseGates": {
            "path": release_gates_path.relative_to(ROOT).as_posix(),
            "sha256": sha256(release_gates_path),
        },
        "packageVersions": dict(sorted(package_versions.items())),
        "artifacts": artifacts,
        "evidence": [
            {
                "profile": profile["id"],
                "status": profile["status"],
            }
            for profile in sorted(
                support_matrix["profiles"], key=lambda profile: profile["id"]
            )
        ],
    }
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(provenance, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(f"Generated release provenance: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
