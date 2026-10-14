import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


PRODUCT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "release_gates", PRODUCT / "tooling" / "audit_release_gates.py",
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleaseGateTests(unittest.TestCase):
    def test_current_gate_manifest_passes(self):
        self.assertEqual(module.main(), 0)

    def test_remote_preview_requires_current_package_and_clean_cache_evidence(self):
        artifacts = PRODUCT / "artifacts"
        artifacts.mkdir(exist_ok=True)
        for missing, blockers in (
            ("fresh-nuget-cache", []),
            ("gradle-plugin-0.1.0-alpha.15", []),
            (None, ["corrected-gradle-plugin-publication"]),
        ):
            with self.subTest(missing=missing, blockers=blockers):
                manifest = json.loads((PRODUCT / "release-gates.json").read_text())
                preview = next(
                    gate for gate in manifest["gates"]
                    if gate["id"] == "remote-preview-consumer"
                )
                if missing:
                    preview["evidence"].remove(missing)
                preview["blockers"] = blockers
                with tempfile.TemporaryDirectory(prefix="release-gates-", dir=artifacts) as work:
                    root = Path(work)
                    (root / "release-gates.json").write_text(json.dumps(manifest))
                    with (
                        patch.object(module, "PRODUCT", root),
                        patch.object(
                            module, "preview_versions",
                            return_value=("0.1.0-alpha.15", "0.1.0-alpha.3", "0.1.0-alpha.10"),
                        ),
                        self.assertRaisesRegex(RuntimeError, "current public package"),
                    ):
                        module.main()

    def test_source_pins_must_match_publication(self):
        with self.assertRaisesRegex(RuntimeError, "Preview plugin pin differs"):
            module.check_preview_pins(
                PRODUCT, "0.1.0-alpha.999", "0.1.0-alpha.3", "0.1.0-alpha.10"
            )

    def test_resolved_swift_version_must_match_publication(self):
        with (
            patch.object(
                module.json, "loads",
                return_value={
                    "pins": [{
                        "identity": "netlift-sdk",
                        "state": {"version": "0.1.0-alpha.9"},
                    }]
                },
            ),
            self.assertRaisesRegex(RuntimeError, "resolved revision differs"),
        ):
            module.check_preview_pins(
                PRODUCT, "0.1.0-alpha.15", "0.1.0-alpha.3", "0.1.0-alpha.10"
            )

    def test_preview_policy_does_not_promote_stable(self):
        manifest = json.loads((PRODUCT / "release-gates.json").read_text())
        manifest["previewPolicy"]["stablePromotionAuthorized"] = True
        with tempfile.TemporaryDirectory(prefix="release-gates-") as work:
            root = Path(work)
            (root / "release-gates.json").write_text(json.dumps(manifest))
            with (
                patch.object(module, "PRODUCT", root),
                patch.object(
                    module, "preview_versions",
                    return_value=("0.1.0-alpha.15", "0.1.0-alpha.3", "0.1.0-alpha.10"),
                ),
                patch.object(module, "check_preview_pins"),
                self.assertRaisesRegex(RuntimeError, "verified android-physical-device"),
            ):
                module.main()

    def test_stable_gate_requires_exact_bytes_and_clean_consumers(self):
        manifest = json.loads((PRODUCT / "release-gates.json").read_text())
        manifest["previewPolicy"]["stablePromotionAuthorized"] = True
        for gate in manifest["gates"]:
            if gate["id"] in module.REQUIRED_STABLE_EVIDENCE:
                gate["status"] = "passed"
                gate["evidence"] = sorted(module.REQUIRED_STABLE_EVIDENCE[gate["id"]])
        promoted = next(
            gate for gate in manifest["gates"]
            if gate["id"] == "stable-byte-promotion"
        )
        promoted["status"] = "passed"
        with tempfile.TemporaryDirectory(prefix="release-gates-", dir=PRODUCT / "artifacts") as work:
            root = Path(work)
            path = root / "release-gates.json"
            with (
                patch.object(module, "PRODUCT", root),
                patch.object(
                    module, "preview_versions",
                    return_value=("0.1.0-alpha.15", "0.1.0-alpha.3", "0.1.0-alpha.10"),
                ),
                patch.object(module, "check_preview_pins"),
            ):
                path.write_text(json.dumps(manifest))
                with self.assertRaisesRegex(RuntimeError, "lacks consumer and identity"):
                    module.main()
                promoted["evidence"] = [
                    "rc-binary-hashes-preserved", "stable-consumer-acceptance"
                ]
                path.write_text(json.dumps(manifest))
                self.assertEqual(module.main(), 0)

    def test_physical_and_rc_evidence_cannot_be_promoted_early(self):
        manifest = json.loads((PRODUCT / "release-gates.json").read_text())
        gates = {gate["id"]: gate for gate in manifest["gates"]}
        gates["android-physical-device"]["status"] = "passed"
        with tempfile.TemporaryDirectory(prefix="release-gates-", dir=PRODUCT / "artifacts") as work:
            root = Path(work)
            path = root / "release-gates.json"
            with (
                patch.object(module, "PRODUCT", root),
                patch.object(
                    module, "preview_versions",
                    return_value=("0.1.0-alpha.15", "0.1.0-alpha.3", "0.1.0-alpha.10"),
                ),
                patch.object(module, "check_preview_pins"),
            ):
                path.write_text(json.dumps(manifest))
                with self.assertRaisesRegex(RuntimeError, "lacks required evidence"):
                    module.main()
                gates["android-physical-device"]["evidence"] = sorted(
                    module.REQUIRED_STABLE_EVIDENCE["android-physical-device"]
                )
                gates["remote-release-candidate-consumers"]["status"] = "passed"
                gates["remote-release-candidate-consumers"]["evidence"] = sorted(
                    module.REQUIRED_STABLE_EVIDENCE["remote-release-candidate-consumers"]
                )
                path.write_text(json.dumps(manifest))
                with self.assertRaisesRegex(RuntimeError, "cannot precede physical"):
                    module.main()

    def test_rc_acceptance_does_not_require_stable_authorization(self):
        manifest = json.loads((PRODUCT / "release-gates.json").read_text())
        for gate in manifest["gates"]:
            if gate["id"] in module.REQUIRED_STABLE_EVIDENCE:
                gate["status"] = "passed"
                gate["evidence"] = sorted(module.REQUIRED_STABLE_EVIDENCE[gate["id"]])
        with tempfile.TemporaryDirectory(prefix="release-gates-", dir=PRODUCT / "artifacts") as work:
            root = Path(work)
            (root / "release-gates.json").write_text(json.dumps(manifest))
            with (
                patch.object(module, "PRODUCT", root),
                patch.object(
                    module, "preview_versions",
                    return_value=("0.1.0-alpha.15", "0.1.0-alpha.3", "0.1.0-alpha.10"),
                ),
                patch.object(module, "check_preview_pins"),
            ):
                self.assertEqual(module.main(), 0)
                promotion = next(
                    gate for gate in manifest["gates"]
                    if gate["id"] == "stable-byte-promotion"
                )
                promotion["status"] = "passed"
                promotion["evidence"] = [
                    "rc-binary-hashes-preserved", "stable-consumer-acceptance"
                ]
                (root / "release-gates.json").write_text(json.dumps(manifest))
                with self.assertRaisesRegex(RuntimeError, "not authorized"):
                    module.main()


if __name__ == "__main__":
    unittest.main()
