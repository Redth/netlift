#!/usr/bin/env python3

from __future__ import annotations

import json
from pathlib import Path


PRODUCT = Path(__file__).resolve().parent
EXPECTED_GATES = {
    "android-signed-bundle-delivery",
    "android-dependency-license-symbol-closure",
    "apple-simulator-release-lifecycle",
    "remote-preview-consumer",
    "android-physical-device",
    "apple-physical-device",
    "remote-release-candidate-consumers",
    "stable-byte-promotion",
}
PASSED_GATES = {
    "android-signed-bundle-delivery",
    "android-dependency-license-symbol-closure",
    "apple-simulator-release-lifecycle",
}


def main() -> int:
    manifest = json.loads(
        (PRODUCT / "release-gates.json").read_text(encoding="utf-8")
    )
    if manifest["schemaVersion"] != 1:
        raise RuntimeError("Unsupported release-gate schema.")
    gates = {gate["id"]: gate for gate in manifest["gates"]}
    if set(gates) != EXPECTED_GATES:
        raise RuntimeError("Release-gate manifest has missing or unexpected gates.")
    for gate_id in PASSED_GATES:
        gate = gates[gate_id]
        if gate["status"] != "passed" or not gate["evidence"]:
            raise RuntimeError(f"Passed release gate lacks evidence: {gate_id}")
    if gates["remote-preview-consumer"]["status"] != "blocked-next-immutable-preview":
        raise RuntimeError("Remote preview must remain blocked until a new version exists.")
    for gate_id in ("android-physical-device", "apple-physical-device"):
        if gates[gate_id]["status"] != "blocked-device-unavailable":
            raise RuntimeError(f"Physical-device gate has an invalid status: {gate_id}")
    for gate_id in ("remote-release-candidate-consumers", "stable-byte-promotion"):
        if gates[gate_id]["status"] != "blocked-prerequisites":
            raise RuntimeError(f"Downstream release gate is promoted early: {gate_id}")
    print("NetLift release-gate audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
