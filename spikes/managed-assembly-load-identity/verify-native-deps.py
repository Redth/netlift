import json
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: verify-native-deps.py <deps.json> <runtime-target-path>", file=sys.stderr)
        return 2

    deps_path = Path(sys.argv[1])
    expected_path = sys.argv[2]
    deps = json.loads(deps_path.read_text(encoding="utf-8"))
    runtime_target = deps.get("runtimeTarget", {}).get("name")
    targets = deps.get("targets", {})
    selected_target = targets.get(runtime_target, {})
    matches = []

    for library_name, library in selected_target.items():
        for asset_path, asset in library.get("runtimeTargets", {}).items():
            if asset_path == expected_path:
                matches.append((library_name, asset_path, asset))

    if len(matches) != 1:
        observed = [
            {
                "target": target_name,
                "library": library_name,
                "path": asset_path,
                "rid": asset.get("rid"),
                "assetType": asset.get("assetType"),
                "localPath": asset.get("localPath"),
            }
            for target_name, libraries in targets.items()
            for library_name, library in libraries.items()
            for asset_path, asset in library.get("runtimeTargets", {}).items()
        ]
        print(
            f"Expected exactly one native asset {expected_path!r} in selected "
            f"target {runtime_target!r}; found {len(matches)}. "
            f"Observed native assets: {json.dumps(observed, sort_keys=True)}",
            file=sys.stderr,
        )
        return 1

    library_name, asset_path, asset = matches[0]
    if (
        asset.get("rid") != "osx-arm64"
        or asset.get("assetType") != "native"
        or asset.get("localPath") != expected_path
    ):
        print(
            f"Native asset mapping is malformed: target={runtime_target!r}, "
            f"library={library_name!r}, path={asset_path!r}, "
            f"metadata={json.dumps(asset, sort_keys=True)}",
            file=sys.stderr,
        )
        return 1

    print(
        f"NATIVE_ASSET_MAP target={runtime_target} library={library_name} "
        f"rid=osx-arm64 path={expected_path}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
