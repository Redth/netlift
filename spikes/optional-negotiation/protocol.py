#!/usr/bin/env python3
"""Small, strict additive-optional-field wire negotiation experiment."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
import re
import struct
from typing import Any


FEATURE_ADDITIVE_OPTIONAL_FIELDS = 1 << 0
WIRE_MAGIC = b"NLF1"
WIRE_VERSION = 1
HEADER = struct.Struct("<4sHHI")
FIELD_HEADER = struct.Struct("<HBI")
MAX_TOTAL_BYTES = 64 * 1024
MAX_FIELDS = 64
MAX_FIELD_BYTES = 16 * 1024
MAX_MANIFEST_BYTES = 16 * 1024
TYPE_CODES = {"utf8": 1, "i32": 2, "bool": 3}
NULL_TYPE_CODE = 0
NAME_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9_]*$")


class ProtocolError(ValueError):
    """Invalid manifest, handshake, or wire payload."""


@dataclass(frozen=True)
class Manifest:
    schema: dict[str, Any]
    schema_hash: str

    @property
    def fields(self) -> dict[int, dict[str, Any]]:
        return {field["id"]: field for field in self.schema["dto"]["fields"]}


@dataclass(frozen=True)
class Negotiated:
    local_hash: str
    peer_hash: str
    local_schema: dict[str, Any]
    peer_schema: dict[str, Any]
    mode: str
    allowed_unknown_optional_ids: frozenset[int]


def canonical_bytes(schema: dict[str, Any]) -> bytes:
    try:
        return json.dumps(
            schema, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
            allow_nan=False,
        ).encode("utf-8", errors="strict")
    except (TypeError, ValueError, UnicodeError) as exc:
        raise ProtocolError(f"schema is not canonical JSON: {exc}") from exc


def schema_hash(schema: dict[str, Any]) -> str:
    return hashlib.sha256(canonical_bytes(schema)).hexdigest()


def _exact_keys(value: dict[str, Any], required: set[str], optional: set[str], where: str) -> None:
    keys = set(value)
    if not required <= keys or keys - required - optional:
        raise ProtocolError(f"{where} has missing or unsupported keys")


def _valid_default(field: dict[str, Any], where: str) -> None:
    if not field["optional"]:
        if "default" in field:
            raise ProtocolError(f"{where}: required field cannot declare a default")
        return
    if "default" not in field:
        raise ProtocolError(f"{where}: optional field needs an explicit supported default")

    default = field["default"]
    if default is None:
        if not field["nullable"]:
            raise ProtocolError(f"{where}: null default requires nullable=true")
        return

    field_type = field["type"]
    if field_type == "utf8" and isinstance(default, str):
        try:
            default.encode("utf-8", errors="strict")
        except UnicodeError as exc:
            raise ProtocolError(f"{where}: default is not valid UTF-8") from exc
    elif field_type == "i32" and type(default) is int and -(2**31) <= default < 2**31:
        return
    elif field_type == "bool" and type(default) is bool:
        return
    else:
        raise ProtocolError(f"{where}: unsupported or out-of-range default")


def validate_schema(schema: dict[str, Any]) -> None:
    if not isinstance(schema, dict):
        raise ProtocolError("schema must be an object")
    _exact_keys(
        schema,
        {"abiMajor", "schemaMajor", "schemaMinor", "serviceId", "dto"},
        set(),
        "schema",
    )
    for key in ("abiMajor", "schemaMajor", "schemaMinor", "serviceId"):
        value = schema[key]
        if type(value) is not int or value < 0 or value > 0xFFFFFFFF:
            raise ProtocolError(f"{key} must be a nonnegative uint32")
    dto = schema["dto"]
    if not isinstance(dto, dict):
        raise ProtocolError("dto must be an object")
    _exact_keys(dto, {"name", "fields"}, set(), "dto")
    if not isinstance(dto["name"], str) or not NAME_PATTERN.fullmatch(dto["name"]):
        raise ProtocolError("dto name is invalid")
    fields = dto["fields"]
    if not isinstance(fields, list) or len(fields) > MAX_FIELDS:
        raise ProtocolError(f"dto fields must be an array of at most {MAX_FIELDS} entries")

    ids: set[int] = set()
    names: set[str] = set()
    for index, field in enumerate(fields):
        where = f"dto.fields[{index}]"
        if not isinstance(field, dict):
            raise ProtocolError(f"{where} must be an object")
        _exact_keys(
            field,
            {"id", "name", "type", "nullable", "optional"},
            {"default"},
            where,
        )
        field_id = field["id"]
        if type(field_id) is not int or field_id < 1 or field_id > 0xFFFF:
            raise ProtocolError(f"{where}.id must be in 1..65535")
        if field_id in ids:
            raise ProtocolError(f"{where}: duplicate field ID {field_id}")
        ids.add(field_id)
        name = field["name"]
        if not isinstance(name, str) or not NAME_PATTERN.fullmatch(name) or name in names:
            raise ProtocolError(f"{where}.name is invalid or duplicated")
        names.add(name)
        if not isinstance(field["type"], str) or field["type"] not in TYPE_CODES:
            raise ProtocolError(f"{where}: unsupported type {field['type']!r}")
        if type(field["nullable"]) is not bool or type(field["optional"]) is not bool:
            raise ProtocolError(f"{where}: nullable and optional must be booleans")
        _valid_default(field, where)


def load_manifest(path: str) -> Manifest:
    def reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ProtocolError(f"duplicate JSON key {key!r}")
            result[key] = value
        return result

    try:
        with open(path, "rb") as source:
            raw = source.read(MAX_MANIFEST_BYTES + 1)
        if len(raw) > MAX_MANIFEST_BYTES:
            raise ProtocolError("manifest exceeds maximum size")
        document = json.loads(
            raw.decode("utf-8", errors="strict"),
            parse_constant=lambda value: (_ for _ in ()).throw(
                ProtocolError(f"invalid JSON numeric constant {value}")
            ),
            object_pairs_hook=reject_duplicate_keys,
        )
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ProtocolError(f"cannot read manifest {path}: {exc}") from exc
    if not isinstance(document, dict):
        raise ProtocolError("manifest must be an object")
    _exact_keys(document, {"schemaHash", "schema"}, set(), "manifest")
    schema = document["schema"]
    validate_schema(schema)
    computed = schema_hash(schema)
    claimed = document["schemaHash"]
    if not isinstance(claimed, str) or not re.fullmatch(r"[0-9a-f]{64}", claimed):
        raise ProtocolError("schemaHash must be lowercase SHA-256 hex")
    if computed != claimed:
        raise ProtocolError("manifest hash does not match canonical schema")
    return Manifest(schema=schema, schema_hash=computed)


def _same_field_contract(left: dict[str, Any], right: dict[str, Any]) -> bool:
    keys = {"id", "name", "type", "nullable", "optional", "default"}
    return all(left.get(key) == right.get(key) for key in keys)


def prove_additive_optional(old: Manifest, new: Manifest) -> frozenset[int]:
    old_schema = old.schema
    new_schema = new.schema
    if (
        old_schema["abiMajor"] != new_schema["abiMajor"]
        or old_schema["schemaMajor"] != new_schema["schemaMajor"]
        or old_schema["serviceId"] != new_schema["serviceId"]
        or old_schema["dto"]["name"] != new_schema["dto"]["name"]
        or old_schema["schemaMinor"] >= new_schema["schemaMinor"]
    ):
        raise ProtocolError("manifests are not an older/newer pair in the same ABI and schema major")

    old_fields = old.fields
    new_fields = new.fields
    for field_id, old_field in old_fields.items():
        new_field = new_fields.get(field_id)
        if new_field is None:
            raise ProtocolError(f"incompatible removal or renumbering of field ID {field_id}")
        if not _same_field_contract(old_field, new_field):
            raise ProtocolError(f"incompatible field contract for ID {field_id}")

    added = set(new_fields) - set(old_fields)
    for field_id in added:
        field = new_fields[field_id]
        if not field["optional"]:
            raise ProtocolError(f"new field ID {field_id} is mandatory")
        _valid_default(field, f"new field ID {field_id}")
    return frozenset(added)


def negotiate(
    local: Manifest,
    peer: Manifest,
    peer_claimed_hash: str,
    *,
    local_opt_in: bool,
    peer_opt_in: bool,
    local_features: int,
    peer_features: int,
) -> Negotiated:
    validate_schema(local.schema)
    validate_schema(peer.schema)
    if type(local_opt_in) is not bool or type(peer_opt_in) is not bool:
        raise ProtocolError("additive opt-in flags must be booleans")
    for name, features in (("local", local_features), ("peer", peer_features)):
        if type(features) is not int or not 0 <= features <= 0xFFFFFFFF:
            raise ProtocolError(f"{name} feature bits must be uint32")
        if features & ~FEATURE_ADDITIVE_OPTIONAL_FIELDS:
            raise ProtocolError(f"{name} advertised unsupported feature bits")
    if schema_hash(local.schema) != local.schema_hash:
        raise ProtocolError("local manifest was modified after validation")
    if schema_hash(peer.schema) != peer.schema_hash or peer_claimed_hash != peer.schema_hash:
        raise ProtocolError("peer manifest/hash mismatch")

    if local.schema_hash == peer.schema_hash:
        if local.schema != peer.schema:
            raise ProtocolError("equal hashes identify different schemas")
        return Negotiated(
            local.schema_hash, peer.schema_hash, local.schema, peer.schema,
            "strict", frozenset(),
        )

    if not local_opt_in or not peer_opt_in:
        raise ProtocolError("schema hash mismatch while additive negotiation is opted out")
    if not (local_features & FEATURE_ADDITIVE_OPTIONAL_FIELDS and
            peer_features & FEATURE_ADDITIVE_OPTIONAL_FIELDS):
        raise ProtocolError("additive optional-field feature was not negotiated by both peers")

    if local.schema["schemaMinor"] < peer.schema["schemaMinor"]:
        allowed_unknown = prove_additive_optional(local, peer)
    else:
        prove_additive_optional(peer, local)
        allowed_unknown = frozenset()

    return Negotiated(
        local.schema_hash, peer.schema_hash, local.schema, peer.schema,
        "additive-optional-v1", allowed_unknown,
    )


def _encode_value(field: dict[str, Any], value: Any) -> tuple[int, bytes]:
    if value is None:
        if not field["nullable"]:
            raise ProtocolError(f"field {field['id']} is not nullable")
        return NULL_TYPE_CODE, b""
    field_type = field["type"]
    if field_type == "utf8" and isinstance(value, str):
        try:
            return TYPE_CODES[field_type], value.encode("utf-8", errors="strict")
        except UnicodeError as exc:
            raise ProtocolError(f"field {field['id']} is not valid UTF-8") from exc
    if field_type == "i32" and type(value) is int and -(2**31) <= value < 2**31:
        return TYPE_CODES[field_type], struct.pack("<i", value)
    if field_type == "bool" and type(value) is bool:
        return TYPE_CODES[field_type], b"\x01" if value else b"\x00"
    raise ProtocolError(f"field {field['id']} has an unsupported or invalid value")


def encode(manifest: Manifest, values: dict[int, Any]) -> bytes:
    validate_schema(manifest.schema)
    if schema_hash(manifest.schema) != manifest.schema_hash:
        raise ProtocolError("manifest changed before encoding")
    if not isinstance(values, dict):
        raise ProtocolError("DTO values must be keyed by field ID")
    fields = manifest.fields
    if any(type(field_id) is not int or field_id not in fields for field_id in values):
        raise ProtocolError("DTO contains an unknown field ID")
    encoded_fields: list[bytes] = []
    for field_id, field in sorted(fields.items()):
        if field_id not in values:
            if field["optional"]:
                continue
            raise ProtocolError(f"required field ID {field_id} is missing")
        wire_type, payload = _encode_value(field, values[field_id])
        if len(payload) > MAX_FIELD_BYTES:
            raise ProtocolError(f"field ID {field_id} exceeds maximum field size")
        encoded_fields.append(FIELD_HEADER.pack(field_id, wire_type, len(payload)) + payload)

    body = b"".join(encoded_fields)
    if len(encoded_fields) > MAX_FIELDS or len(body) > MAX_TOTAL_BYTES - HEADER.size:
        raise ProtocolError("encoded DTO exceeds maximum size")
    return HEADER.pack(WIRE_MAGIC, WIRE_VERSION, len(encoded_fields), len(body)) + body


def _decode_value(field: dict[str, Any], wire_type: int, payload: bytes) -> Any:
    if wire_type == NULL_TYPE_CODE:
        if payload or not field["nullable"]:
            raise ProtocolError(f"field ID {field['id']} has invalid null encoding")
        return None

    expected = TYPE_CODES[field["type"]]
    if wire_type != expected:
        raise ProtocolError(f"field ID {field['id']} has incompatible wire type")
    if field["type"] == "utf8":
        try:
            return payload.decode("utf-8", errors="strict")
        except UnicodeError as exc:
            raise ProtocolError(f"field ID {field['id']} contains malformed UTF-8") from exc
    if field["type"] == "i32":
        if len(payload) != 4:
            raise ProtocolError(f"field ID {field['id']} must contain exactly four bytes")
        return struct.unpack("<i", payload)[0]
    if field["type"] == "bool":
        if len(payload) != 1 or payload[0] not in (0, 1):
            raise ProtocolError(f"field ID {field['id']} has malformed bool encoding")
        return payload[0] == 1
    raise ProtocolError(f"field ID {field['id']} has unsupported type")


def decode(data: bytes, local: Manifest, negotiated: Negotiated) -> dict[int, Any]:
    if not isinstance(data, bytes) or len(data) < HEADER.size or len(data) > MAX_TOTAL_BYTES:
        raise ProtocolError("wire payload has invalid total length")
    validate_schema(local.schema)
    validate_schema(negotiated.peer_schema)
    if schema_hash(local.schema) != local.schema_hash:
        raise ProtocolError("local manifest changed after validation")
    if negotiated.local_hash != local.schema_hash:
        raise ProtocolError("negotiation does not belong to this local manifest")
    if (negotiated.local_schema != local.schema or
            schema_hash(negotiated.local_schema) != negotiated.local_hash):
        raise ProtocolError("negotiation's local manifest changed after validation")
    if schema_hash(negotiated.peer_schema) != negotiated.peer_hash:
        raise ProtocolError("validated peer manifest changed after negotiation")
    if negotiated.mode == "strict":
        if (negotiated.peer_hash != local.schema_hash or
                negotiated.peer_schema != local.schema or
                negotiated.allowed_unknown_optional_ids):
            raise ProtocolError("strict negotiation has a schema mismatch or skip allowance")
    elif negotiated.mode == "additive-optional-v1":
        if local.schema_hash == negotiated.peer_hash:
            raise ProtocolError("additive negotiation unexpectedly has an exact schema hash")
        if local.schema["schemaMinor"] < negotiated.peer_schema["schemaMinor"]:
            expected_unknown = prove_additive_optional(
                local, Manifest(negotiated.peer_schema, negotiated.peer_hash)
            )
        else:
            prove_additive_optional(
                Manifest(negotiated.peer_schema, negotiated.peer_hash), local
            )
            expected_unknown = frozenset()
        if expected_unknown != negotiated.allowed_unknown_optional_ids:
            raise ProtocolError("negotiation skip set does not match additive proof")
    else:
        raise ProtocolError("unsupported negotiation mode")

    magic, version, field_count, body_length = HEADER.unpack_from(data)
    if magic != WIRE_MAGIC or version != WIRE_VERSION:
        raise ProtocolError("wire magic or version mismatch")
    if field_count > MAX_FIELDS or body_length != len(data) - HEADER.size:
        raise ProtocolError("wire field count or body length is invalid")

    local_fields = local.fields
    peer_fields = {
        field["id"]: field for field in negotiated.peer_schema["dto"]["fields"]
    }
    values: dict[int, Any] = {}
    seen: set[int] = set()
    offset = HEADER.size
    for _ in range(field_count):
        if len(data) - offset < FIELD_HEADER.size:
            raise ProtocolError("truncated field header")
        field_id, wire_type, field_length = FIELD_HEADER.unpack_from(data, offset)
        offset += FIELD_HEADER.size
        if field_id == 0 or field_id in seen:
            raise ProtocolError("zero or duplicate field ID")
        seen.add(field_id)
        if field_length > MAX_FIELD_BYTES or field_length > len(data) - offset:
            raise ProtocolError("field length exceeds bounds or remaining payload")
        payload = data[offset:offset + field_length]
        offset += field_length

        peer_field = peer_fields.get(field_id)
        if peer_field is None:
            raise ProtocolError(f"field ID {field_id} is absent from validated peer manifest")

        local_field = local_fields.get(field_id)
        if local_field is not None:
            values[field_id] = _decode_value(local_field, wire_type, payload)
            continue

        if not peer_field["optional"]:
            raise ProtocolError(f"unknown field ID {field_id} is mandatory in peer manifest")
        if field_id not in negotiated.allowed_unknown_optional_ids:
            raise ProtocolError(f"unknown field ID {field_id} was not authorized by additive negotiation")
        _decode_value(peer_field, wire_type, payload)

    if offset != len(data):
        raise ProtocolError("trailing bytes after declared fields")

    for field_id, field in local_fields.items():
        if field_id not in values:
            if not field["optional"]:
                raise ProtocolError(f"required field ID {field_id} is missing")
            values[field_id] = field["default"]
    return values
