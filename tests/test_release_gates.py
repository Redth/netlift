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
            ("gradle-plugin-0.1.0-alpha.14", []),
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
                        self.assertRaisesRegex(RuntimeError, "current public package"),
                    ):
                        module.main()


if __name__ == "__main__":
    unittest.main()
