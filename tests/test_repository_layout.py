import re
import subprocess
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import unquote


ROOT = Path(__file__).resolve().parents[1]


def source_files():
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT, check=True, capture_output=True, text=True,
    )
    return {
        ROOT / name for name in result.stdout.split("\0")
        if name and (ROOT / name).is_file()
    }


class RepositoryLayoutTests(unittest.TestCase):
    def test_only_canonical_source_roots_remain(self):
        for name in ("product", "experiments", "spikes"):
            with self.subTest(name=name):
                self.assertFalse((ROOT / name).exists())
        for name in ("src", "tests", "samples", "android", "apple", "tooling"):
            with self.subTest(name=name):
                self.assertTrue((ROOT / name).is_dir())

    def test_solution_and_project_references_resolve(self):
        solution = ET.parse(ROOT / "NetLift.slnx")
        for project in solution.findall(".//Project"):
            self.assertTrue((ROOT / project.attrib["Path"]).is_file())
        for path in source_files():
            if path.suffix != ".csproj":
                continue
            for reference in ET.parse(path).findall(".//ProjectReference"):
                target = (path.parent / reference.attrib["Include"]).resolve()
                with self.subTest(project=path.relative_to(ROOT), reference=target):
                    self.assertTrue(target.is_file())
                    self.assertTrue(target.is_relative_to(ROOT))

    def test_documentation_links_resolve(self):
        for path in source_files():
            if path.suffix != ".md":
                continue
            text = re.sub(r"```.*?```", "", path.read_text(), flags=re.DOTALL)
            for target in re.findall(r"\]\(([^)\s]+)\)", text):
                if ":" in target or target.startswith("#"):
                    continue
                target = unquote(target.split("#", 1)[0])
                with self.subTest(document=path.relative_to(ROOT), target=target):
                    self.assertTrue((path.parent / target).exists())


if __name__ == "__main__":
    unittest.main()
