#!/usr/bin/env python3
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path


LOCK_FIELDS = {"format", "old", "new"}
SCHEMA_FIELDS = {"schemaHash", "schema"}
HASH_RE = re.compile(r"[0-9a-f]{64}\Z")
CANONICAL_RE = re.compile(
    r'\bcanonicalJson\b\s*(?::\s*String)?\s*=\s*("(?:\\.|[^"\\])*")'
)
SCHEMA_HASH_RE = re.compile(
    r'\bschemaHash\b\s*(?::\s*String)?\s*=\s*"([0-9a-f]{64})"'
)
UINT32_MAX = (1 << 32) - 1


class StageError(Exception):
    pass


def strict_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate property {key}")
        result[key] = value
    return result


def load_json(data, label):
    try:
        return json.loads(
            data,
            object_pairs_hook=strict_object,
            parse_constant=lambda value: (_ for _ in ()).throw(
                ValueError(f"non-standard JSON number {value}")
            ),
        )
    except (json.JSONDecodeError, ValueError) as error:
        raise StageError(f"{label} is invalid JSON: {error}") from error


def remove_output(path):
    try:
        shutil.rmtree(path)
    except FileNotFoundError:
        return
    except OSError as error:
        raise StageError(f"could not remove incomplete output {path}: {error}") from error


def digest_bytes(data):
    return hashlib.sha256(data).hexdigest()


def validate_lock(data):
    lock = load_json(data, "async codec lock")
    if not isinstance(lock, dict) or set(lock) != LOCK_FIELDS:
        raise StageError("async codec lock must contain exactly format, old, and new")
    if lock["format"] != "netlift-v5-async-native-codec-lock-v1":
        raise StageError("unsupported async codec lock format")
    for direction in ("old", "new"):
        entry = lock[direction]
        if not isinstance(entry, dict) or set(entry) != {
            "schemaHash", "contractRevision", "swiftSha256", "kotlinSha256"
        }:
            raise StageError(f"{direction} lock entry has an unexpected shape")
        for key in ("schemaHash", "swiftSha256", "kotlinSha256"):
            if not isinstance(entry[key], str) or HASH_RE.fullmatch(entry[key]) is None:
                raise StageError(f"{direction}.{key} must be lowercase full SHA-256")
        revision = entry["contractRevision"]
        if (
            not isinstance(revision, dict)
            or set(revision) != {"major", "minor"}
            or type(revision["major"]) is not int
            or type(revision["minor"]) is not int
            or not 1 <= revision["major"] <= UINT32_MAX
            or not 0 <= revision["minor"] <= UINT32_MAX
        ):
            raise StageError(f"{direction}.contractRevision is invalid")
    return lock


def source_string(source, label, pattern):
    match = pattern.search(source)
    if match is None:
        raise StageError(f"{label} lacks the expected generated constant")
    try:
        return json.loads(match.group(1))
    except json.JSONDecodeError as error:
        raise StageError(f"{label} string literal is invalid: {error}") from error


def reject_unsupported(value, path="$.schema"):
    if value is None or isinstance(value, (bool, str)) or type(value) is int:
        return
    if isinstance(value, list):
        for index, item in enumerate(value):
            reject_unsupported(item, f"{path}[{index}]")
        return
    if isinstance(value, dict):
        if any(not isinstance(key, str) for key in value):
            raise StageError(f"unsupported schema property at {path}")
        for key, item in value.items():
            reject_unsupported(item, f"{path}.{key}")
        return
    raise StageError(f"unsupported schema value at {path}: {type(value).__name__}")


def validate_source(source_bytes, label, language, lock_entry):
    try:
        source = source_bytes.decode("utf-8")
    except UnicodeDecodeError as error:
        raise StageError(f"{label} is not UTF-8: {error}") from error
    language_sha = lock_entry[f"{language}Sha256"]
    actual_sha = digest_bytes(source_bytes)
    if actual_sha != language_sha:
        raise StageError(f"{label} bytes disagree with the app-owned source pin")

    canonical_json = source_string(source, label, CANONICAL_RE)
    hash_match = SCHEMA_HASH_RE.search(source)
    if hash_match is None:
        raise StageError(f"{label} lacks the expected generated schema hash")
    declared_hash = hash_match.group(1)
    envelope = load_json(canonical_json, f"{label} canonicalJson")
    if not isinstance(envelope, dict) or set(envelope) != SCHEMA_FIELDS:
        raise StageError(f"{label} canonicalJson must contain schemaHash and schema only")
    schema_hash = envelope["schemaHash"]
    schema = envelope["schema"]
    if (
        not isinstance(schema_hash, str)
        or HASH_RE.fullmatch(schema_hash) is None
        or not isinstance(schema, dict)
    ):
        raise StageError(f"{label} canonical schema envelope is malformed")
    reject_unsupported(schema)
    try:
        canonical_schema = json.dumps(
            schema,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
            allow_nan=False,
        ).encode("utf-8")
    except (TypeError, ValueError, UnicodeEncodeError) as error:
        raise StageError(f"{label} schema cannot be canonically encoded: {error}") from error
    if digest_bytes(canonical_schema) != schema_hash or declared_hash != schema_hash:
        raise StageError(f"{label} canonical schema hash is inconsistent")
    if schema_hash != lock_entry["schemaHash"]:
        raise StageError(f"{label} full schema hash disagrees with async codec lock")
    if schema.get("profile") != "async-callback-v1":
        raise StageError(f"{label} profile is not async-callback-v1")
    if schema.get("schemaVersion") != 5 or schema.get("featureBits") != 0x36 or \
            schema.get("requiredFeatureBits") != 0x36:
        raise StageError(f"{label} profile/version/features are outside the frozen boundary")
    revision = schema.get("contractRevision")
    if revision != lock_entry["contractRevision"]:
        raise StageError(f"{label} contract revision disagrees with async codec lock")
    return canonical_json, schema_hash, revision


def validate_api(source, language):
    if language == "swift":
        signatures = (
            r"encodeRequest\s*\(\s*_\s*value:\s*SubmitInput,"
            r"\s*context:\s*NetLiftV5PeerContext\s*\)"
            r"\s*throws\s*->\s*\[UInt8\]",
            r"decodeResult\s*\(\s*_\s*source:\s*\[UInt8\],"
            r"\s*context:\s*ManagedResultSchemaContext\s*\)"
            r"\s*throws\s*->\s*SubmitResult",
        )
    else:
        signatures = (
            r"encodeRequest\s*\(\s*value:\s*SubmitInput,"
            r"\s*context:\s*NegotiatedPeerContext\s*\)"
            r"\s*:\s*ByteArray",
            r"decodeResult\s*\(\s*source:\s*ByteArray,"
            r"\s*context:\s*ManagedResultSchemaContext\s*\)"
            r"\s*:\s*SubmitResult",
        )
    for signature in signatures:
        if re.search(signature, source) is None:
            raise StageError(f"generated {language} source lacks async host codec API")


def input_digest(entries):
    digest = hashlib.sha256()
    for label, content in entries:
        name = label.encode("ascii")
        digest.update(len(name).to_bytes(4, "big"))
        digest.update(name)
        digest.update(len(content).to_bytes(8, "big"))
        digest.update(content)
    return digest.hexdigest()


def atomic_write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as temporary:
        temporary.write(data)
        temp_path = Path(temporary.name)
    os.replace(temp_path, path)


def main():
    if len(sys.argv) != 7:
        raise StageError(
            "usage: stage-async-codec.py <lock> <old|new> <swift|kotlin> "
            "<source-dir> <consumer-template> <output-dir>"
        )
    lock_path, direction, language, source_dir, consumer_path, output_dir = sys.argv[1:]
    lock_path = Path(lock_path)
    source_dir = Path(source_dir)
    consumer_path = Path(consumer_path)
    output_dir = Path(output_dir)
    if direction not in ("old", "new") or language not in ("swift", "kotlin"):
        raise StageError("direction must be old|new and language must be swift|kotlin")
    try:
        lock_bytes = lock_path.read_bytes()
        swift_bytes = (source_dir / "NetLift.swift").read_bytes()
        kotlin_bytes = (source_dir / "NetLift.kt").read_bytes()
        consumer_bytes = consumer_path.read_bytes()
    except OSError as error:
        remove_output(output_dir)
        raise StageError(f"cannot read pinned async codec inputs: {error}") from error

    try:
        lock = validate_lock(lock_bytes)
        lock_entry = lock[direction]
        swift_text = swift_bytes.decode("utf-8")
        kotlin_text = kotlin_bytes.decode("utf-8")
        swift_canonical, swift_hash, revision = validate_source(
            swift_bytes, "generated Swift", "swift", lock_entry
        )
        kotlin_canonical, kotlin_hash, kotlin_revision = validate_source(
            kotlin_bytes, "generated Kotlin", "kotlin", lock_entry
        )
        validate_api(swift_text, "swift")
        validate_api(kotlin_text, "kotlin")
        if swift_canonical != kotlin_canonical or swift_hash != kotlin_hash or \
                revision != kotlin_revision:
            raise StageError("Swift and Kotlin async codecs disagree on schema bytes")
        source_bytes = swift_bytes if language == "swift" else kotlin_bytes
        source_text = source_bytes.decode("utf-8")
        validate_api(source_text, language)
        consumer_text = consumer_bytes.decode("utf-8")
        if "testingOnly" in consumer_text or "testingOnly" in source_text:
            raise StageError("testing-only context factories are not allowed in this fixture")

        generated_name = "NetLift.swift" if language == "swift" else "NetLift.kt"
        consumer_name = "NativeMain.swift" if language == "swift" else "CodecConsumer.kt"
        manifest_name = "async-codec-provenance.json"
        generated_path = output_dir / generated_name
        consumer_output = output_dir / consumer_name
        manifest_path = output_dir / manifest_name
        digest = input_digest((
            ("async-codec-lock", lock_bytes),
            ("generated-swift", swift_bytes),
            ("generated-kotlin", kotlin_bytes),
            ("consumer-template", consumer_bytes),
            ("direction", direction.encode("ascii")),
            ("language", language.encode("ascii")),
        ))
        manifest = {
            "format": "netlift-v5-async-native-codec-stage-v1",
            "direction": direction,
            "language": language,
            "schemaHash": swift_hash,
            "contractRevision": revision,
            "swiftSha256": digest_bytes(swift_bytes),
            "kotlinSha256": digest_bytes(kotlin_bytes),
            "consumerSha256": digest_bytes(consumer_bytes),
            "inputsSha256": digest,
        }
        manifest_bytes = (
            json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n"
        ).encode("utf-8")
        if (
            generated_path.is_file()
            and generated_path.read_bytes() == source_bytes
            and consumer_output.is_file()
            and consumer_output.read_bytes() == consumer_bytes
            and manifest_path.is_file()
            and manifest_path.read_bytes() == manifest_bytes
        ):
            print(f"V5-ASYNC-CODEC-SKIP {direction}/{language} inputsSha256={digest}")
            return

        remove_output(output_dir)
        atomic_write(generated_path, source_bytes)
        atomic_write(consumer_output, consumer_bytes)
        atomic_write(manifest_path, manifest_bytes)
    except (OSError, UnicodeDecodeError) as error:
        remove_output(output_dir)
        raise StageError(f"could not stage async codec outputs: {error}") from error
    except StageError:
        remove_output(output_dir)
        raise
    print(f"V5-ASYNC-CODEC-STAGE {direction}/{language} inputsSha256={digest}")


if __name__ == "__main__":
    try:
        main()
    except StageError as error:
        print(f"async-codec: {error}", file=sys.stderr)
        raise SystemExit(2)
