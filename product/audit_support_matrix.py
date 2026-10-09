#!/usr/bin/env python3

from __future__ import annotations

import json
from pathlib import Path


PRODUCT = Path(__file__).resolve().parent


def main() -> int:
    matrix = json.loads(
        (PRODUCT / "support-matrix.json").read_text(encoding="utf-8")
    )
    if matrix["schemaVersion"] != 1:
        raise RuntimeError("Unsupported support-matrix schema.")
    profiles = {profile["id"]: profile for profile in matrix["profiles"]}
    expected = {
        "android-portable",
        "android-view-peer",
        "apple-portable",
        "apple-uiview-peer",
    }
    if set(profiles) != expected:
        raise RuntimeError("Support matrix has missing or unexpected profiles.")
    for peer_id in ("android-view-peer", "apple-uiview-peer"):
        peer = profiles[peer_id]
        if peer["status"] != "experimental-device-required":
            raise RuntimeError(
                f"{peer_id} claims support without physical-device evidence."
            )
        if not peer["excludedCapabilities"]:
            raise RuntimeError(f"{peer_id} must declare excluded capabilities.")
    for portable_id in ("android-portable", "apple-portable"):
        portable = profiles[portable_id]
        if portable["status"] != "supported-preview":
            raise RuntimeError(f"{portable_id} has an unexpected support level.")
        if not portable["physicalDeviceRequiredForStable"]:
            raise RuntimeError(
                f"{portable_id} must retain the stable physical-device gate."
            )
    print("NetLift support-matrix audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
