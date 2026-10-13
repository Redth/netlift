import argparse
import importlib.util
import unittest
from pathlib import Path
from unittest.mock import Mock, patch


spec = importlib.util.spec_from_file_location(
    "android_component",
    Path(__file__).parents[1] / "android" / "tooling" / "build_component.py",
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AndroidComponentBuildTests(unittest.TestCase):
    def test_self_contained_property_is_global_during_restore_and_build(self):
        root = Path(__file__).resolve().parent
        for configuration in ("Debug", "Release"):
            with self.subTest(configuration=configuration):
                args = argparse.Namespace(
                    project=root / "Component.csproj",
                    generated=root / "generated",
                    output=root / "component.aar",
                    configuration=configuration,
                    target_framework="net11.0-android",
                    runtime_identifier="android-arm64",
                )
                inspector = Mock()
                with (
                    patch.object(module, "parse_args", return_value=args),
                    patch.object(module, "load_workload_inspector", return_value=inspector),
                    patch.object(module, "run", side_effect=RuntimeError("stop after build command")) as run,
                    self.assertRaisesRegex(RuntimeError, "stop after build command"),
                ):
                    module.main()
                run.assert_called_once_with(
                    "dotnet",
                    "build",
                    str(args.project),
                    "-c",
                    configuration,
                    "-f",
                    args.target_framework,
                    "-r",
                    args.runtime_identifier,
                    "-p:SelfContained=true",
                    f"-p:NetLiftGeneratedDirectory={args.generated}",
                    "-v:minimal",
                    cwd=root,
                )


if __name__ == "__main__":
    unittest.main()
