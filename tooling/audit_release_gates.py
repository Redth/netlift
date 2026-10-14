#!/usr/bin/env python3

from __future__ import annotations

import json
import re
from pathlib import Path


PRODUCT = Path(__file__).resolve().parents[1]
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
    "remote-preview-consumer",
}
REQUIRED_PREVIEW_EVIDENCE = {
    "preview-package-pins-verified",
    "fresh-gradle-cache",
    "fresh-nuget-cache",
    "fresh-swiftpm-cache",
    "prior-preview-consumer-upgrade",
    "public-feed-only-consumer-build",
    "public-feed-only-emulator-acceptance",
    "public-package-source-owned-ios-host-acceptance",
    "r8-release-execution",
    "published-artifact-byte-identity",
    "process-relaunch",
}


def preview_versions(root: Path) -> tuple[str, str, str]:
    properties = dict(
        line.split("=", 1)
        for line in (root / "android/gradle.properties").read_text().splitlines()
        if line and not line.startswith("#")
    )
    swift = (root / "apple/stage_public_preview.py").read_text()
    bundle = (root / "apple/build_tool_bundle.py").read_text()
    versions = re.findall(r'^VERSION = "(0\.1\.0-alpha\.\d+)"$', swift, re.MULTILINE)
    if len(versions) != 1 or f'VERSION = "{versions[0]}"' not in bundle:
        raise RuntimeError("Swift publication and tool bundle versions differ.")
    return properties["netliftPluginVersion"], properties["netliftVersion"], versions[0]


def check_preview_pins(root: Path, plugin: str, runtime: str, swift: str) -> None:
    for name in (
        "samples/HelloWorld.Android/app/build.gradle.kts",
        "samples/Orders.Android/app/build.gradle.kts",
        "samples/MauiEmbedding/Compose/app/build.gradle.kts",
    ):
        text = (root / name).read_text()
        if f'id("io.github.redth.netlift") version "{plugin}"' not in text:
            raise RuntimeError(f"Preview plugin pin differs from publication: {name}")
    package = (root / "apple/samples/OrdersPreview/Package.swift").read_text()
    if f'exact: "{swift}"' not in package:
        raise RuntimeError("Swift sample pin differs from publication.")
    android_plugin = (root / "android/netlift-gradle-plugin/src/main/kotlin/dev/netlift/gradle/NetLiftPlugin.kt").read_text()
    if f'extension.runtimeVersion.convention("{runtime}")' not in android_plugin:
        raise RuntimeError("Plugin runtime dependency differs from publication.")


def main() -> int:
    manifest = json.loads(
        (PRODUCT / "release-gates.json").read_text(encoding="utf-8")
    )
    if manifest["schemaVersion"] != 1:
        raise RuntimeError("Unsupported release-gate schema.")
    if manifest["previewPolicy"] != {
        "physicalDeviceEvidenceRequired": False,
        "stablePromotionAuthorized": False,
    }:
        raise RuntimeError("Preview cannot require devices or authorize stable promotion.")
    gates = {gate["id"]: gate for gate in manifest["gates"]}
    if set(gates) != EXPECTED_GATES:
        raise RuntimeError("Release-gate manifest has missing or unexpected gates.")
    for gate_id in PASSED_GATES:
        gate = gates[gate_id]
        if gate["status"] != "passed" or not gate["evidence"]:
            raise RuntimeError(f"Passed release gate lacks evidence: {gate_id}")
    preview = gates["remote-preview-consumer"]
    if (
        not REQUIRED_PREVIEW_EVIDENCE.issubset(preview["evidence"])
        or preview.get("blockers")
    ):
        raise RuntimeError(
            "Remote preview requires the current public package and clean-cache acceptance."
        )
    plugin, runtime, swift = preview_versions(PRODUCT)
    if not {f"gradle-plugin-{plugin}", f"swift-package-{swift}"}.issubset(
        preview["evidence"]
    ):
        raise RuntimeError(
            "Remote preview requires the current public package and clean-cache acceptance."
        )
    check_preview_pins(PRODUCT, plugin, runtime, swift)
    for gate_id in ("android-physical-device", "apple-physical-device"):
        if (gates[gate_id]["status"] != "blocked-device-unavailable"
                or gates[gate_id].get("requiredForStable") is not True):
            raise RuntimeError(f"Physical-device gate has an invalid status: {gate_id}")
    for gate_id in ("remote-release-candidate-consumers", "stable-byte-promotion"):
        if gates[gate_id]["status"] != "blocked-prerequisites":
            raise RuntimeError(f"Downstream release gate is promoted early: {gate_id}")
    print("NetLift release-gate audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
