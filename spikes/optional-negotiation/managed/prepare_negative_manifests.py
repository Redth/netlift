#!/usr/bin/env python3
"""Create hash-valid incompatible manifests for the native-host test cases."""

import copy
import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / ".artifacts"


def canonical_bytes(schema: dict) -> bytes:
    return json.dumps(
        schema, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
        allow_nan=False,
    ).encode("utf-8")


def write_manifest(name: str, schema: dict) -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    document = {
        "schemaHash": hashlib.sha256(canonical_bytes(schema)).hexdigest(),
        "schema": schema,
    }
    (OUTPUT / name).write_text(
        json.dumps(document, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


source = json.loads((ROOT / "v2.schema.json").read_text(encoding="utf-8"))["schema"]

mandatory = copy.deepcopy(source)
mandatory["dto"]["fields"][2]["optional"] = False
mandatory["dto"]["fields"][2].pop("default")
write_manifest("mandatory-addition.schema.json", mandatory)

unsupported_default = copy.deepcopy(source)
unsupported_default["dto"]["fields"][2]["default"] = {"not": "a scalar"}
write_manifest("unsupported-default.schema.json", unsupported_default)

same_minor = copy.deepcopy(source)
same_minor["schemaMinor"] = 0
write_manifest("same-minor-addition.schema.json", same_minor)

abi_major = copy.deepcopy(source)
abi_major["abiMajor"] = 2
write_manifest("abi-major-mismatch.schema.json", abi_major)

schema_major = copy.deepcopy(source)
schema_major["schemaMajor"] = 2
write_manifest("schema-major-mismatch.schema.json", schema_major)

renumbered = copy.deepcopy(source)
renumbered["dto"]["fields"][0]["id"] = 5
write_manifest("renumbered-field.schema.json", renumbered)

type_changed = copy.deepcopy(source)
type_changed["dto"]["fields"][1]["type"] = "bool"
write_manifest("changed-type.schema.json", type_changed)

nullability_changed = copy.deepcopy(source)
nullability_changed["dto"]["fields"][0]["nullable"] = True
write_manifest("changed-nullability.schema.json", nullability_changed)
