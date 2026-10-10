#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

from generate_release_provenance import ROOT, directory_sha256, sha256


COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")
HASH_PATTERN = re.compile(r"^[0-9a-f]{64}$")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("manifest", type=Path)
    return parser.parse_args()


def resolve_repository_path(value: str) -> Path:
    path = Path(value)
    if path.is_absolute():
        raise RuntimeError(f"Provenance path must be repository-relative: {value}")
    resolved = (ROOT / path).resolve()
    try:
        resolved.relative_to(ROOT)
    except ValueError as error:
        raise RuntimeError(f"Provenance path escapes the repository: {value}") from error
    return resolved


def main() -> int:
    args = parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    if manifest["schemaVersion"] != 1:
        raise RuntimeError("Unsupported release-provenance schema.")
    if not COMMIT_PATTERN.fullmatch(manifest["sourceCommit"]):
        raise RuntimeError("Release provenance has an invalid source commit.")
    if not manifest["packageVersions"]:
        raise RuntimeError("Release provenance has no package versions.")

    support = manifest["supportMatrix"]
    support_path = resolve_repository_path(support["path"])
    if not HASH_PATTERN.fullmatch(support["sha256"]):
        raise RuntimeError("Support-matrix hash is invalid.")
    if sha256(support_path) != support["sha256"]:
        raise RuntimeError("Support-matrix hash does not match.")
    support_matrix = json.loads(support_path.read_text(encoding="utf-8"))
    expected_evidence = [
        {"profile": profile["id"], "status": profile["status"]}
        for profile in sorted(
            support_matrix["profiles"], key=lambda profile: profile["id"]
        )
    ]
    if manifest["evidence"] != expected_evidence:
        raise RuntimeError("Release evidence does not match the support matrix.")

    release_gates = manifest["releaseGates"]
    release_gates_path = resolve_repository_path(release_gates["path"])
    if not HASH_PATTERN.fullmatch(release_gates["sha256"]):
        raise RuntimeError("Release-gate hash is invalid.")
    if sha256(release_gates_path) != release_gates["sha256"]:
        raise RuntimeError("Release-gate hash does not match.")

    ids: set[str] = set()
    for artifact in manifest["artifacts"]:
        artifact_id = artifact["id"]
        if artifact_id in ids:
            raise RuntimeError(f"Duplicate artifact ID: {artifact_id}")
        ids.add(artifact_id)
        path = resolve_repository_path(artifact["path"])
        expected_kind = "file" if path.is_file() else "directory" if path.is_dir() else None
        if expected_kind is None:
            raise RuntimeError(f"Release artifact is missing: {path}")
        if artifact["kind"] != expected_kind:
            raise RuntimeError(f"Release artifact kind is incorrect: {artifact_id}")
        actual_hash = sha256(path) if path.is_file() else directory_sha256(path)
        if actual_hash != artifact["sha256"]:
            raise RuntimeError(f"Release artifact hash does not match: {artifact_id}")

    if not ids:
        raise RuntimeError("Release provenance has no artifacts.")
    print(f"NetLift release-provenance audit passed: {args.manifest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
