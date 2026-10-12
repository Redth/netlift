#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import re
import subprocess
from pathlib import Path


def properties(project: Path, configuration: str, framework: str | None = None) -> dict[str, str]:
    command = [
        "dotnet", "msbuild", str(project), "-nologo",
        f"-p:Configuration={configuration}",
        "-getProperty:TargetFramework,TargetFrameworks,TargetPath,UseMaui,MauiVersion",
    ]
    if framework:
        command.append(f"-p:TargetFramework={framework}")
    result = subprocess.run(
        command, cwd=project.parent, check=True, text=True, stdout=subprocess.PIPE,
    )
    return json.loads(result.stdout)["Properties"]


def select_framework(frameworks: list[str], component: str, explicit: str | None = None) -> str:
    match = re.fullmatch(r"net(\d+)\.(\d+)-(ios|android)(?:[\d.]+)?", component)
    if not match:
        raise ValueError(f"Unsupported component framework: {component}")
    component_version = (int(match[1]), int(match[2]))
    if explicit:
        if explicit not in frameworks:
            raise ValueError(f"Requested framework {explicit} is not declared: {', '.join(frameworks)}")
        parsed = re.fullmatch(r"net(\d+)\.(\d+)(?:-(ios|android)([\d.]+)?)?", explicit)
        if (not parsed or (int(parsed[1]), int(parsed[2])) > component_version or
                parsed[3] not in (None, match[3])):
            raise ValueError(f"Requested framework {explicit} is incompatible with {component}")
        return explicit
    candidates: list[str] = []
    for framework in frameworks:
        parsed = re.fullmatch(r"net(\d+)\.(\d+)(?:-(ios|android)([\d.]+)?)?", framework)
        if parsed and (int(parsed[1]), int(parsed[2])) <= component_version:
            if parsed[3] == match[3]:
                candidates.append(framework)
    if not candidates:
        candidates = [
            framework for framework in frameworks
            if (parsed := re.fullmatch(r"net(\d+)\.(\d+)", framework))
            and (int(parsed[1]), int(parsed[2])) <= component_version
        ]
    if len(candidates) != 1:
        raise ValueError(
            f"Expected one compatible {match[3]} or portable framework for {component}; "
            f"found {candidates}. Set the managed framework explicitly. Declared: {frameworks}"
        )
    return candidates[0]


def inspect_project(
    project: Path,
    component: str,
    configuration: str,
    explicit: str | None = None,
) -> dict[str, str]:
    evaluated = properties(project, configuration)
    frameworks = [
        value for value in (
            evaluated["TargetFrameworks"] or evaluated["TargetFramework"]
        ).split(";") if value
    ]
    selected = select_framework(frameworks, component, explicit)
    result = properties(project, configuration, selected)
    result["SelectedTargetFramework"] = selected
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Select and inspect a managed project for a native component.")
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--component-framework", required=True)
    parser.add_argument("--managed-framework")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = inspect_project(
        args.project.resolve(), args.component_framework, args.configuration, args.managed_framework,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
