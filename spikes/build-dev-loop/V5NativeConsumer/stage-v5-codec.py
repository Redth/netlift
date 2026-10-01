#!/usr/bin/env python3
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path


LOCK_KEYS = (
    "format",
    "expectedOldSchemaSha256",
    "expectedOldContractRevision",
    "expectedNewSchemaSha256",
    "expectedNewContractRevision",
)
HASH_PATTERN = re.compile(r"[0-9a-f]{64}\Z")
REVISION_PATTERN = re.compile(r"([1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
CANONICAL_SOURCE_PATTERN = re.compile(
    r'public const string CanonicalJson = ("(?:\\.|[^"\\])*");'
)
SCHEMA_HASH_SOURCE_PATTERN = re.compile(
    r'public const string SchemaHash = "([0-9a-f]{64})";'
)
CODEC_CANONICAL_PATTERN = re.compile(
    r'\bcanonicalJson\b[^=\n]*=\s*("(?:\\.|[^"\\])*")'
)
CODEC_HASH_PATTERN = re.compile(
    r'\bschemaHash\b[^=\n]*=\s*"([0-9a-f]{64})"'
)
UINT32_MAX = (1 << 32) - 1


class StageError(Exception):
    pass


def reject_duplicate_keys(pairs):
    values = {}
    for key, value in pairs:
        if key in values:
            raise ValueError(f"duplicate JSON property: {key}")
        values[key] = value
    return values


def reject_nonstandard_number(value):
    raise ValueError(f"non-standard JSON number: {value}")


def strict_json_loads(source):
    return json.loads(
        source,
        object_pairs_hook=reject_duplicate_keys,
        parse_constant=reject_nonstandard_number,
    )


def reject_unsupported_schema_value(value, path="$.schema"):
    if value is None or isinstance(value, (bool, str)) or type(value) is int:
        return
    if isinstance(value, list):
        for index, item in enumerate(value):
            reject_unsupported_schema_value(item, f"{path}[{index}]")
        return
    if isinstance(value, dict):
        if any(not isinstance(key, str) for key in value):
            raise StageError(f"unsupported non-string schema property at {path}")
        for key, item in value.items():
            reject_unsupported_schema_value(item, f"{path}.{key}")
        return
    raise StageError(f"unsupported schema value at {path}: {type(value).__name__}")


def parse_revision(value, name):
    match = REVISION_PATTERN.fullmatch(value)
    if match is None or len(match.group(1)) > 10 or len(match.group(2)) > 10:
        raise StageError(f"{name} must be a uint32 major.minor revision")
    major, minor = int(match.group(1)), int(match.group(2))
    if major > UINT32_MAX or minor > UINT32_MAX:
        raise StageError(f"{name} components must fit uint32")
    return major, minor


def parse_lock(data):
    try:
        lines = data.decode("utf-8").splitlines()
    except UnicodeDecodeError as error:
        raise StageError(f"native codec schema lock is not UTF-8: {error}") from error
    pairs = []
    for line_number, line in enumerate(lines, 1):
        if not line or "=" not in line:
            raise StageError(f"native codec schema lock line {line_number} must be key=value")
        key, value = line.split("=", 1)
        if not key or not value or key.strip() != key or value.strip() != value:
            raise StageError(
                f"native codec schema lock line {line_number} has invalid whitespace/data"
            )
        pairs.append((key, value))
    if tuple(key for key, _ in pairs) != LOCK_KEYS:
        raise StageError(
            "native codec schema lock keys/order do not match "
            "netlift-v5-native-codec-schema-v1"
        )
    lock = dict(pairs)
    if lock["format"] != "netlift-v5-native-codec-schema-v1":
        raise StageError("unsupported native codec schema lock format")
    for direction in ("Old", "New"):
        hash_key = f"expected{direction}SchemaSha256"
        if not HASH_PATTERN.fullmatch(lock[hash_key]):
            raise StageError(f"{hash_key} must be a lowercase full SHA-256 digest")
        revision_key = f"expected{direction}ContractRevision"
        lock[revision_key] = parse_revision(lock[revision_key], revision_key)
    return lock


def parse_contract(path, source_bytes):
    try:
        source = source_bytes.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise StageError(f"{path.name} is not UTF-8: {error}") from error
    canonical_match = CANONICAL_SOURCE_PATTERN.search(source)
    hash_match = SCHEMA_HASH_SOURCE_PATTERN.search(source)
    if canonical_match is None or hash_match is None:
        raise StageError(f"{path.name} lacks generated CanonicalJson or SchemaHash")
    try:
        canonical_json = json.loads(canonical_match.group(1))
        envelope = strict_json_loads(canonical_json)
    except (json.JSONDecodeError, ValueError) as error:
        raise StageError(f"{path.name} has invalid CanonicalJson: {error}") from error
    if not isinstance(envelope, dict) or set(envelope) != {"schemaHash", "schema"}:
        raise StageError(f"{path.name} CanonicalJson must contain only schemaHash and schema")
    schema_hash = envelope["schemaHash"]
    schema = envelope["schema"]
    if not isinstance(schema, dict):
        raise StageError(f"{path.name} CanonicalJson schema must be an object")
    reject_unsupported_schema_value(schema)
    try:
        canonical_schema = json.dumps(
            schema,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
            allow_nan=False,
        ).encode("utf-8")
    except (TypeError, ValueError, UnicodeEncodeError) as error:
        raise StageError(f"{path.name} schema cannot be canonically encoded: {error}") from error
    computed_hash = hashlib.sha256(canonical_schema).hexdigest()
    if not isinstance(schema_hash, str) or not HASH_PATTERN.fullmatch(schema_hash):
        raise StageError(f"{path.name} schemaHash must be lowercase SHA-256 hex")
    if hash_match.group(1) != schema_hash:
        raise StageError(f"{path.name} SchemaHash constant disagrees with CanonicalJson")
    if computed_hash != schema_hash:
        raise StageError(f"{path.name} canonical schema SHA-256 disagrees with schemaHash")
    revision = schema.get("contractRevision")
    if not isinstance(revision, dict):
        raise StageError(f"{path.name} lacks a contractRevision object")
    major, minor = revision.get("major"), revision.get("minor")
    if (
        type(major) is not int
        or type(minor) is not int
        or major < 1
        or minor < 0
        or major > UINT32_MAX
        or minor > UINT32_MAX
    ):
        raise StageError(f"{path.name} contract revision components must fit uint32")
    return canonical_json, schema_hash, (major, minor)


def input_digest(inputs):
    digest = hashlib.sha256()
    for label, content in inputs:
        name = label.encode("ascii")
        digest.update(len(name).to_bytes(4, "big"))
        digest.update(name)
        digest.update(len(content).to_bytes(8, "big"))
        digest.update(content)
    return digest.hexdigest()


def remove_output(output_dir):
    try:
        shutil.rmtree(output_dir)
    except FileNotFoundError:
        return
    except OSError as error:
        raise StageError(f"could not remove incomplete output {output_dir}: {error}") from error


def write_atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as temporary:
        temporary.write(data)
        temporary_path = Path(temporary.name)
    os.replace(temporary_path, path)


def main():
    if len(sys.argv) != 8:
        raise StageError(
            "usage: stage-v5-codec.py <lock> <old|new> <swift|kotlin> "
            "<generated-contract.g.cs> <projection-inputs.sha256> <emitted-dir> <output-dir>"
        )
    lock_path, direction, language, contract_path, projection_inputs_path, emitted_dir, output_dir = (
        Path(value) if index != 1 and index != 2 else value
        for index, value in enumerate(sys.argv[1:])
    )
    if direction not in ("old", "new") or language not in ("swift", "kotlin"):
        raise StageError("direction must be old|new and language must be swift|kotlin")
    output_dir.mkdir(parents=True, exist_ok=True)
    target_name = "NetLift.swift" if language == "swift" else "NetLift.kt"
    target_path = output_dir / target_name
    manifest_path = output_dir / "codec-provenance.json"

    try:
        lock_bytes = lock_path.read_bytes()
        contract_bytes = contract_path.read_bytes()
        projection_inputs = projection_inputs_path.read_bytes().strip()
        swift_bytes = (emitted_dir / "NetLift.swift").read_bytes()
        kotlin_bytes = (emitted_dir / "NetLift.kt").read_bytes()
        emitted_names = sorted(path.name for path in emitted_dir.iterdir())
    except OSError as error:
        remove_output(output_dir)
        raise StageError(f"cannot read complete v5 codec inputs: {error}") from error
    if emitted_names != ["NetLift.kt", "NetLift.swift"]:
        remove_output(output_dir)
        raise StageError("v5 emitter must produce exactly NetLift.swift and NetLift.kt")
    try:
        projection_hash = projection_inputs.decode("ascii")
    except UnicodeDecodeError as error:
        remove_output(output_dir)
        raise StageError("projection input fingerprint is not ASCII SHA-256 hex") from error
    if not HASH_PATTERN.fullmatch(projection_hash):
        remove_output(output_dir)
        raise StageError("projection input fingerprint must be lowercase SHA-256 hex")

    inputs_hash = input_digest((
        ("native-codec-schema-lock", lock_bytes),
        ("generated-contract", contract_bytes),
        ("projection-source-fingerprint", projection_inputs),
        ("generated-swift", swift_bytes),
        ("generated-kotlin", kotlin_bytes),
        ("stage-helper", Path(__file__).read_bytes()),
        ("direction", direction.encode("ascii")),
        ("language", language.encode("ascii")),
    ))
    try:
        previous = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError, OSError, UnicodeDecodeError):
        previous = {}
    if not isinstance(previous, dict):
        previous = {}
    if not isinstance(previous, dict) or previous.get("inputsSha256") != inputs_hash:
        remove_output(output_dir)

    try:
        lock = parse_lock(lock_bytes)
        canonical_json, schema_hash, revision = parse_contract(contract_path, contract_bytes)
        direction_title = direction.title()
        if schema_hash != lock[f"expected{direction_title}SchemaSha256"]:
            raise StageError(
                f"{direction} generated schema hash disagrees with native codec schema lock"
            )
        if revision != lock[f"expected{direction_title}ContractRevision"]:
            raise StageError(
                f"{direction} generated contract revision disagrees with "
                "native codec schema lock"
            )
        language_bytes = swift_bytes if language == "swift" else kotlin_bytes
        language_source = language_bytes.decode("utf-8")
        host_context_signature = (
            r"encodePayload\(_ value: Payload, context: NetLiftV5PeerContext\)"
            if language == "swift"
            else r"encodePayload\(value: Payload, context: NegotiatedPeerContext\): ByteArray"
        )
        if re.search(host_context_signature, language_source) is None:
            raise StageError(
                f"generated {language} codec does not expose the host-bound peer-context API"
            )
        canonical_match = CODEC_CANONICAL_PATTERN.search(language_source)
        hash_match = CODEC_HASH_PATTERN.search(language_source)
        if canonical_match is None or hash_match is None:
            raise StageError(f"generated {language} codec lacks canonicalJson or schemaHash")
        try:
            emitted_canonical = json.loads(canonical_match.group(1))
        except json.JSONDecodeError as error:
            raise StageError(f"generated {language} canonicalJson is invalid: {error}") from error
        if emitted_canonical != canonical_json or hash_match.group(1) != schema_hash:
            raise StageError(f"generated {language} codec does not embed the pinned v5 manifest")
        metadata = {
            "format": "netlift-v5-native-codec-v1",
            "direction": direction,
            "language": language,
            "schemaHash": schema_hash,
            "contractRevision": {"major": revision[0], "minor": revision[1]},
            "projectionInputsSha256": projection_hash,
            "inputsSha256": inputs_hash,
            "codecSha256": hashlib.sha256(language_bytes).hexdigest(),
        }
        manifest = (json.dumps(metadata, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
        if (
            previous.get("inputsSha256") == inputs_hash
            and target_path.is_file()
            and target_path.read_bytes() == language_bytes
            and manifest_path.is_file()
            and manifest_path.read_bytes() == manifest
        ):
            print(f"V5-CODEC-SKIP {direction}/{language} inputsSha256={inputs_hash}")
            return
        remove_output(output_dir)
        output_dir.mkdir(parents=True, exist_ok=True)
        write_atomic(target_path, language_bytes)
        write_atomic(manifest_path, manifest)
    except (OSError, UnicodeDecodeError) as error:
        remove_output(output_dir)
        raise StageError(f"could not stage generated v5 codec: {error}") from error
    except StageError:
        remove_output(output_dir)
        raise
    print(f"V5-CODEC-STAGE {direction}/{language} inputsSha256={inputs_hash}")


if __name__ == "__main__":
    try:
        main()
    except StageError as error:
        print(f"v5-codec: {error}", file=sys.stderr)
        raise SystemExit(2)
