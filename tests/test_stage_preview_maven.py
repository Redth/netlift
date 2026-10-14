import hashlib
import importlib.util
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "stage_preview_maven", ROOT / "tooling/stage_preview_maven.py"
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def metadata(version: str) -> str:
    return (
        "<metadata><versioning><latest>" + version + "</latest><release>"
        + version + "</release><versions><version>" + version
        + "</version></versions><lastUpdated>20261014000000"
        + "</lastUpdated></versioning></metadata>"
    )


class StagePreviewMavenTests(unittest.TestCase):
    def test_preserves_old_versions_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "product"
            sdk = root / "sdk"
            (sdk / ".git").mkdir(parents=True)
            (source / "android").mkdir(parents=True)
            (source / "android/gradle.properties").write_text(
                "netliftPluginVersion=0.1.0-alpha.15\n"
            )
            for name in module.ARTIFACTS:
                local = source / "artifacts/maven" / module.GROUP / name
                published = sdk / "maven" / module.GROUP / name
                (local / "0.1.0-alpha.15").mkdir(parents=True)
                (published / "0.1.0-alpha.14").mkdir(parents=True)
                artifact = local / "0.1.0-alpha.15" / f"{name}.pom"
                artifact.write_bytes(b"new package")
                (artifact.parent / f"{name}.pom.sha256").write_text(
                    hashlib.sha256(artifact.read_bytes()).hexdigest()
                )
                (local / "maven-metadata.xml").write_text(metadata("0.1.0-alpha.15"))
                (published / "maven-metadata.xml").write_text(
                    metadata("0.1.0-alpha.14")
                )
            with patch.object(module, "ROOT", source):
                module.stage(sdk)
                with self.assertRaisesRegex(RuntimeError, "Refusing to overwrite"):
                    module.stage(sdk)
            for name in module.ARTIFACTS:
                published = sdk / "maven" / module.GROUP / name
                tree = ET.parse(published / "maven-metadata.xml")
                self.assertEqual(
                    [item.text for item in tree.findall("versioning/versions/version")],
                    ["0.1.0-alpha.14", "0.1.0-alpha.15"],
                )
                self.assertTrue((published / "0.1.0-alpha.14").is_dir())
                self.assertEqual(
                    (published / "0.1.0-alpha.15" / f"{name}.pom").read_bytes(),
                    b"new package",
                )


if __name__ == "__main__":
    unittest.main()
