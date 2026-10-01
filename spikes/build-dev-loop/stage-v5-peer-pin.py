#!/usr/bin/env python3
import hashlib
import json
import os
import re
import sys
import tempfile
from pathlib import Path


EXPECTED_KEYS = (
    "format",
    "expectedOldPeerSchemaSha256",
    "minimumOldPeerContractRevision",
    "expectedNewPeerSchemaSha256",
    "minimumNewPeerContractRevision",
)
HASH_PATTERN = re.compile(r"[0-9a-f]{64}\Z")
REVISION_PATTERN = re.compile(r"([1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
UINT32_MAX = (1 << 32) - 1
CANONICAL_JSON_PATTERN = re.compile(
    r'public const string CanonicalJson = ("(?:\\.|[^"\\])*");'
)
SCHEMA_HASH_PATTERN = re.compile(r'public const string SchemaHash = "([0-9a-f]{64})";')


def fail(message):
    print(f"peer-pin: {message}", file=sys.stderr)
    raise SystemExit(2)


def reject_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON object key: {key}")
        result[key] = value
    return result


def reject_nonstandard_number(value):
    raise ValueError(f"non-standard JSON number: {value}")


def parse_json(data):
    return json.loads(
        data,
        object_pairs_hook=reject_duplicate_keys,
        parse_constant=reject_nonstandard_number,
    )


def validate_schema_value(value, path="$"):
    if value is None or isinstance(value, (bool, str)) or type(value) is int:
        return
    if isinstance(value, list):
        for index, item in enumerate(value):
            validate_schema_value(item, f"{path}[{index}]")
        return
    if isinstance(value, dict):
        for key, item in value.items():
            if not isinstance(key, str):
                fail(f"schema object key at {path} is not a string")
            validate_schema_value(item, f"{path}.{key}")
        return
    fail(f"unsupported canonical schema value at {path}: {type(value).__name__}")


def parse_lock(data):
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        fail("lock is not UTF-8")
    pairs = []
    for line_number, line in enumerate(text.splitlines(), 1):
        if not line or "=" not in line:
            fail(f"lock line {line_number} must be key=value")
        key, value = line.split("=", 1)
        if not key or not value or key.strip() != key or value.strip() != value:
            fail(f"lock line {line_number} has empty or whitespace-padded data")
        pairs.append((key, value))
    if tuple(key for key, _ in pairs) != EXPECTED_KEYS:
        fail("lock keys, order, or version do not match netlift-peer-provenance-v1")
    lock = dict(pairs)
    if lock["format"] != "netlift-peer-provenance-v1":
        fail("unsupported lock format")
    for key in ("expectedOldPeerSchemaSha256", "expectedNewPeerSchemaSha256"):
        if not HASH_PATTERN.fullmatch(lock[key]):
            fail(f"{key} must be a lowercase full SHA-256 digest")
    for key in ("minimumOldPeerContractRevision", "minimumNewPeerContractRevision"):
        match = REVISION_PATTERN.fullmatch(lock[key])
        if not match:
            fail(f"{key} must be a canonical major.minor revision")
        if len(match.group(1)) > 10 or len(match.group(2)) > 10:
            fail(f"{key} components must fit uint32")
        revision = (int(match.group(1)), int(match.group(2)))
        if any(component > UINT32_MAX for component in revision):
            fail(f"{key} components must fit uint32")
        lock[key] = revision
    return lock


def parse_generated_contract(path):
    source = path.read_text(encoding="utf-8-sig")
    canonical_match = CANONICAL_JSON_PATTERN.search(source)
    hash_match = SCHEMA_HASH_PATTERN.search(source)
    if canonical_match is None or hash_match is None:
        fail(f"{path.name} lacks generated CanonicalJson or SchemaHash")
    try:
        canonical_json = json.loads(canonical_match.group(1))
        canonical = parse_json(canonical_json)
    except (json.JSONDecodeError, ValueError) as error:
        fail(f"{path.name} has invalid generated CanonicalJson: {error}")
    if not isinstance(canonical, dict) or set(canonical) != {"schemaHash", "schema"}:
        fail(f"{path.name} CanonicalJson must contain only schemaHash and schema")
    schema_hash = canonical.get("schemaHash")
    schema = canonical.get("schema")
    if not isinstance(schema, dict):
        fail(f"{path.name} CanonicalJson schema payload must be an object")
    validate_schema_value(schema, "$.schema")
    try:
        canonical_schema = json.dumps(
            schema,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
            allow_nan=False,
        ).encode("utf-8")
    except (TypeError, ValueError, UnicodeEncodeError) as error:
        fail(f"{path.name} schema cannot be canonically encoded as UTF-8: {error}")
    computed_schema_hash = hashlib.sha256(canonical_schema).hexdigest()
    if not isinstance(schema_hash, str) or not HASH_PATTERN.fullmatch(schema_hash):
        fail(f"{path.name} CanonicalJson schemaHash is not a full lowercase SHA-256 digest")
    if hash_match.group(1) != schema_hash:
        fail(f"{path.name} SchemaHash constant does not match CanonicalJson schemaHash")
    if computed_schema_hash != schema_hash:
        fail(f"{path.name} CanonicalJson schema SHA-256 does not match declared schemaHash")
    revision = schema.get("contractRevision")
    if not isinstance(revision, dict):
        fail(f"{path.name} CanonicalJson lacks a contractRevision object")
    if (
        type(revision.get("major")) is not int
        or type(revision.get("minor")) is not int
        or revision["major"] < 1
        or revision["minor"] < 0
        or revision["major"] > UINT32_MAX
        or revision["minor"] > UINT32_MAX
    ):
        fail(f"{path.name} contract revision components must fit uint32")
    return {
        "schemaHash": schema_hash,
        "contractMajor": revision["major"],
        "contractMinor": revision["minor"],
    }


def revision_not_below_floor(actual, floor):
    return actual["contractMajor"] > floor[0] or (
        actual["contractMajor"] == floor[0] and actual["contractMinor"] >= floor[1]
    )


def emit_pin(lock, old_peer, new_peer):
    expected = (
        ("expectedOldPeerSchemaSha256", old_peer["schemaHash"], "old peer"),
        ("expectedNewPeerSchemaSha256", new_peer["schemaHash"], "new peer"),
    )
    for key, actual_hash, name in expected:
        if lock[key] != actual_hash:
            fail(f"{key} does not match generated {name} CanonicalJson schemaHash")
    floors = (
        ("minimumOldPeerContractRevision", lock["minimumOldPeerContractRevision"], old_peer, "old peer"),
        ("minimumNewPeerContractRevision", lock["minimumNewPeerContractRevision"], new_peer, "new peer"),
    )
    for key, floor, peer, name in floors:
        if not revision_not_below_floor(peer, floor):
            fail(f"{key} is above the generated {name} contract revision")

    macros = [
        ("NETLIFT_OLD_PEER_SCHEMA_SHA256", f'"{lock["expectedOldPeerSchemaSha256"]}"'),
        ("NETLIFT_OLD_PEER_CONTRACT_MAJOR", f'{old_peer["contractMajor"]}u'),
        ("NETLIFT_OLD_PEER_CONTRACT_MINOR", f'{old_peer["contractMinor"]}u'),
        ("NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MAJOR", f"{lock['minimumOldPeerContractRevision'][0]}u"),
        ("NETLIFT_OLD_PEER_MINIMUM_CONTRACT_MINOR", f"{lock['minimumOldPeerContractRevision'][1]}u"),
        ("NETLIFT_NEW_PEER_SCHEMA_SHA256", f'"{lock["expectedNewPeerSchemaSha256"]}"'),
        ("NETLIFT_NEW_PEER_CONTRACT_MAJOR", f'{new_peer["contractMajor"]}u'),
        ("NETLIFT_NEW_PEER_CONTRACT_MINOR", f'{new_peer["contractMinor"]}u'),
        ("NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MAJOR", f"{lock['minimumNewPeerContractRevision'][0]}u"),
        ("NETLIFT_NEW_PEER_MINIMUM_CONTRACT_MINOR", f"{lock['minimumNewPeerContractRevision'][1]}u"),
    ]
    header = (
        "#ifndef NETLIFT_PEER_PROVENANCE_H\n"
        "#define NETLIFT_PEER_PROVENANCE_H\n\n"
        + "".join(f"#define {name} {value}\n" for name, value in macros)
        + "\n#endif\n"
    ).encode("ascii")
    return header


def digest_inputs(inputs):
    digest = hashlib.sha256()
    for name, content in inputs:
        encoded_name = name.encode("ascii")
        digest.update(len(encoded_name).to_bytes(4, "big"))
        digest.update(encoded_name)
        digest.update(len(content).to_bytes(8, "big"))
        digest.update(content)
    return digest.hexdigest()


def write_atomic(path, data):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as temporary:
        temporary.write(data)
        temporary_path = Path(temporary.name)
    os.replace(temporary_path, path)


def main():
    if len(sys.argv) != 5:
        fail("usage: stage-v5-peer-pin.py <lock> <old-LiftContract.g.cs> <new-LiftContract.g.cs> <output-dir>")
    lock_path, old_path, new_path, output_path = map(Path, sys.argv[1:])
    output_path.mkdir(parents=True, exist_ok=True)
    header_path = output_path / "peer_provenance.h"
    manifest_path = output_path / "peer_provenance.manifest.json"
    try:
        lock_bytes = lock_path.read_bytes()
        old_bytes = old_path.read_bytes()
        new_bytes = new_path.read_bytes()
    except OSError as error:
        header_path.unlink(missing_ok=True)
        manifest_path.unlink(missing_ok=True)
        fail(f"cannot read a lock or generated contract input: {error}")
    inputs = (
        ("app-owned-lock", lock_bytes),
        ("old-generated-contract", old_bytes),
        ("new-generated-contract", new_bytes),
    )
    input_hash = digest_inputs(inputs)

    try:
        previous = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        previous = {}
    if not isinstance(previous, dict):
        previous = {}

    inputs_unchanged = previous.get("inputsSha256") == input_hash
    if not inputs_unchanged:
        header_path.unlink(missing_ok=True)
        manifest_path.unlink(missing_ok=True)
    lock = parse_lock(lock_bytes)
    old_peer = parse_generated_contract(old_path)
    new_peer = parse_generated_contract(new_path)
    header = emit_pin(lock, old_peer, new_peer)
    metadata = {
        "format": "netlift-peer-pin-stage-v1",
        "inputsSha256": input_hash,
        "headerSha256": hashlib.sha256(header).hexdigest(),
        "oldPeer": {
            **old_peer,
            "minimumContractRevision": list(lock["minimumOldPeerContractRevision"]),
        },
        "newPeer": {
            **new_peer,
            "minimumContractRevision": list(lock["minimumNewPeerContractRevision"]),
        },
    }
    manifest = (json.dumps(metadata, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    if inputs_unchanged:
        try:
            if header_path.read_bytes() == header and manifest_path.read_bytes() == manifest:
                print(f"PIN-STAGE-SKIP inputsSha256={input_hash}")
                return
        except OSError:
            pass
        header_path.unlink(missing_ok=True)
        manifest_path.unlink(missing_ok=True)
    write_atomic(header_path, header)
    write_atomic(manifest_path, manifest)
    print(f"PIN-STAGE-RUN inputsSha256={input_hash} headerSha256={metadata['headerSha256']}")


if __name__ == "__main__":
    main()
