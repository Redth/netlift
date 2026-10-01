from __future__ import annotations

import copy
import json
from pathlib import Path
import struct
import unittest

from protocol import (
    FEATURE_ADDITIVE_OPTIONAL_FIELDS,
    FIELD_HEADER,
    HEADER,
    MAX_TOTAL_BYTES,
    Manifest,
    Negotiated,
    ProtocolError,
    decode,
    encode,
    load_manifest,
    negotiate,
    schema_hash,
    validate_schema,
)


ROOT = Path(__file__).parent
V1 = load_manifest(str(ROOT / "v1.schema.json"))
V2 = load_manifest(str(ROOT / "v2.schema.json"))
FEATURES = FEATURE_ADDITIVE_OPTIONAL_FIELDS


def negotiated(local, peer, *, opt_in=True, peer_opt_in=True, local_features=FEATURES,
               peer_features=FEATURES, claimed_hash=None):
    return negotiate(
        local, peer, peer.schema_hash if claimed_hash is None else claimed_hash,
        local_opt_in=opt_in,
        peer_opt_in=peer_opt_in,
        local_features=local_features,
        peer_features=peer_features,
    )


def document(schema: dict) -> Manifest:
    validate_schema(schema)
    return Manifest(schema=schema, schema_hash=schema_hash(schema))


def with_field(payload: bytes, field_id: int, wire_type: int, value: bytes) -> bytes:
    magic, version, count, body_len = HEADER.unpack_from(payload)
    body = payload[HEADER.size:] + FIELD_HEADER.pack(field_id, wire_type, len(value)) + value
    return HEADER.pack(magic, version, count + 1, body_len + FIELD_HEADER.size + len(value)) + body


def replace_field(payload: bytes, target_id: int, wire_type: int, value: bytes) -> bytes:
    magic, version, count, _ = HEADER.unpack_from(payload)
    offset = HEADER.size
    body = bytearray()
    replaced = False
    for _ in range(count):
        field_id, current_type, current_length = FIELD_HEADER.unpack_from(payload, offset)
        offset += FIELD_HEADER.size
        current_value = payload[offset:offset + current_length]
        offset += current_length
        if field_id == target_id:
            current_type = wire_type
            current_value = value
            replaced = True
        body += FIELD_HEADER.pack(field_id, current_type, len(current_value)) + current_value
    if not replaced:
        raise AssertionError(f"field ID {target_id} was not present")
    return HEADER.pack(magic, version, count, len(body)) + body


class NegotiationTests(unittest.TestCase):
    def test_manifest_hashes_are_distinct_and_pinned(self) -> None:
        self.assertNotEqual(V1.schema_hash, V2.schema_hash)
        self.assertEqual(schema_hash(V1.schema), V1.schema_hash)
        self.assertEqual(schema_hash(V2.schema), V2.schema_hash)

    def test_strict_exact_hash_handshake_works_without_opt_in(self) -> None:
        session = negotiated(V1, V1, opt_in=False, peer_opt_in=False,
                             local_features=0, peer_features=0)
        self.assertEqual(session.mode, "strict")
        self.assertEqual(session.allowed_unknown_optional_ids, frozenset())

    def test_opt_out_keeps_hash_mismatch_strict(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "opted out"):
            negotiated(V1, V2, opt_in=False)

    def test_both_peers_must_negotiate_feature(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "feature"):
            negotiated(V1, V2, peer_features=0)
        with self.assertRaisesRegex(ProtocolError, "feature"):
            negotiated(V1, V2, local_features=0)
        with self.assertRaisesRegex(ProtocolError, "opted out"):
            negotiated(V1, V2, peer_opt_in=False)

    def test_opt_in_flags_do_not_use_truthy_coercion(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "must be booleans"):
            negotiated(V1, V2, opt_in="yes")

    def test_additive_pair_proves_only_new_optional_ids(self) -> None:
        session = negotiated(V1, V2)
        self.assertEqual(session.mode, "additive-optional-v1")
        self.assertEqual(session.allowed_unknown_optional_ids, frozenset({7, 9}))

    def test_old_payload_decodes_new_with_declared_defaults(self) -> None:
        old_values = {1: "SKU-42", 2: 4}
        old_payload = encode(V1, old_values)
        session = negotiated(V2, V1)
        decoded = decode(old_payload, V2, session)
        self.assertEqual(decoded, {1: "SKU-42", 2: 4, 7: "", 9: False})

    def test_new_payload_decodes_old_skipping_manifest_optional_fields(self) -> None:
        new_values = {1: "SKU-43", 2: 8, 7: "gift note", 9: True}
        new_payload = encode(V2, new_values)
        session = negotiated(V1, V2)
        decoded = decode(new_payload, V1, session)
        self.assertEqual(decoded, {1: "SKU-43", 2: 8})

    def test_cross_version_round_trip_preserves_shared_fields(self) -> None:
        old_values = {1: "SKU-41", 2: 6}
        new_decoded = decode(encode(V1, old_values), V2, negotiated(V2, V1))
        returned_to_old = decode(encode(V2, new_decoded), V1, negotiated(V1, V2))
        self.assertEqual(returned_to_old, old_values)

        new_values = {1: "SKU-44", 2: 2, 7: "handle with care", 9: True}
        old_decoded = decode(encode(V2, new_values), V1, negotiated(V1, V2))
        returned_to_new = decode(encode(V1, old_decoded), V2, negotiated(V2, V1))
        self.assertEqual(returned_to_new, {1: "SKU-44", 2: 2, 7: "", 9: False})

    def test_null_and_empty_string_are_distinct(self) -> None:
        schema = copy.deepcopy(V2.schema)
        note = schema["dto"]["fields"][2]
        note["nullable"] = True
        note["default"] = None
        nullable_manifest = document(schema)
        session = negotiated(nullable_manifest, nullable_manifest)
        with_null = encode(nullable_manifest, {1: "SKU", 2: 1, 7: None, 9: False})
        with_empty = encode(nullable_manifest, {1: "SKU", 2: 1, 7: "", 9: False})
        self.assertNotEqual(with_null, with_empty)
        self.assertIsNone(decode(with_null, nullable_manifest, session)[7])
        self.assertEqual(decode(with_empty, nullable_manifest, session)[7], "")

    def test_unknown_id_not_in_peer_manifest_is_rejected(self) -> None:
        payload = with_field(encode(V2, {1: "SKU", 2: 1}), 11, 1, b"no")
        with self.assertRaisesRegex(ProtocolError, "absent from validated peer"):
            decode(payload, V1, negotiated(V1, V2))

    def test_local_known_field_absent_from_peer_manifest_is_rejected(self) -> None:
        payload = encode(V2, {1: "SKU", 2: 1, 7: "V2-only"})
        session = negotiated(V2, V1)
        with self.assertRaisesRegex(ProtocolError, "absent from validated peer"):
            decode(payload, V2, session)

    def test_unknown_field_with_mismatched_peer_wire_type_is_rejected(self) -> None:
        payload = with_field(encode(V2, {1: "SKU", 2: 1}), 7, 2, b"\0\0\0\0")
        with self.assertRaisesRegex(ProtocolError, "wire type"):
            decode(payload, V1, negotiated(V1, V2))

    def test_mandatory_addition_is_rejected_before_decoder_is_enabled(self) -> None:
        schema = copy.deepcopy(V2.schema)
        field = schema["dto"]["fields"][2]
        field["optional"] = False
        field.pop("default")
        bad_peer = document(schema)
        with self.assertRaisesRegex(ProtocolError, "mandatory"):
            negotiated(V1, bad_peer)

    def test_decoder_defensively_rejects_unproven_mandatory_unknown(self) -> None:
        schema = copy.deepcopy(V2.schema)
        field = schema["dto"]["fields"][2]
        field["optional"] = False
        field.pop("default")
        bad_peer = document(schema)
        forged = Negotiated(
            V1.schema_hash,
            bad_peer.schema_hash,
            V1.schema,
            bad_peer.schema,
            "additive-optional-v1",
            frozenset({7}),
        )
        payload = with_field(encode(V1, {1: "SKU", 2: 1}), 7, 1, b"mandatory")
        with self.assertRaisesRegex(ProtocolError, "mandatory"):
            decode(payload, V1, forged)

    def test_peer_manifest_hash_claim_mismatch_is_rejected(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "manifest/hash mismatch"):
            negotiated(V1, V2, claimed_hash="0" * 64)

    def test_manifest_content_hash_mismatch_is_rejected(self) -> None:
        schema = copy.deepcopy(V2.schema)
        schema["schemaMinor"] = 2
        wrong_hash_doc = ROOT / ".artifacts" / "bad-manifest.json"
        wrong_hash_doc.parent.mkdir(parents=True, exist_ok=True)
        wrong_hash_doc.write_text(
            json.dumps({"schemaHash": V2.schema_hash, "schema": schema}),
            encoding="utf-8",
        )
        try:
            with self.assertRaisesRegex(ProtocolError, "manifest hash"):
                load_manifest(str(wrong_hash_doc))
        finally:
            wrong_hash_doc.unlink(missing_ok=True)

    def test_duplicate_json_manifest_keys_are_rejected(self) -> None:
        duplicate_doc = ROOT / ".artifacts" / "duplicate-key-manifest.json"
        duplicate_doc.parent.mkdir(parents=True, exist_ok=True)
        duplicate_doc.write_text(
            '{"schemaHash":"x","schemaHash":"y","schema":{}}',
            encoding="utf-8",
        )
        try:
            with self.assertRaisesRegex(ProtocolError, "duplicate JSON key"):
                load_manifest(str(duplicate_doc))
        finally:
            duplicate_doc.unlink(missing_ok=True)

    def test_removed_or_renumbered_field_is_rejected(self) -> None:
        schema = copy.deepcopy(V2.schema)
        schema["dto"]["fields"][0]["id"] = 5
        bad_peer = document(schema)
        with self.assertRaisesRegex(ProtocolError, "removal or renumbering"):
            negotiated(V1, bad_peer)

    def test_existing_field_type_change_is_rejected(self) -> None:
        schema = copy.deepcopy(V2.schema)
        schema["dto"]["fields"][1]["type"] = "bool"
        bad_peer = document(schema)
        with self.assertRaisesRegex(ProtocolError, "field contract"):
            negotiated(V1, bad_peer)

    def test_existing_field_nullability_change_is_rejected(self) -> None:
        schema = copy.deepcopy(V2.schema)
        schema["dto"]["fields"][0]["nullable"] = True
        bad_peer = document(schema)
        with self.assertRaisesRegex(ProtocolError, "field contract"):
            negotiated(V1, bad_peer)

    def test_unsupported_type_and_default_are_rejected(self) -> None:
        unsupported_type = copy.deepcopy(V2.schema)
        unsupported_type["dto"]["fields"][2]["type"] = "decimal"
        with self.assertRaisesRegex(ProtocolError, "unsupported type"):
            document(unsupported_type)

        unsupported_default = copy.deepcopy(V2.schema)
        unsupported_default["dto"]["fields"][2]["default"] = {"text": "not scalar"}
        with self.assertRaisesRegex(ProtocolError, "unsupported or out-of-range default"):
            document(unsupported_default)

    def test_duplicate_known_id_is_rejected(self) -> None:
        payload = with_field(encode(V1, {1: "SKU", 2: 1}), 1, 1, b"duplicate")
        with self.assertRaisesRegex(ProtocolError, "duplicate field ID"):
            decode(payload, V1, negotiated(V1, V1))

    def test_malformed_lengths_and_trailing_bytes_are_rejected(self) -> None:
        payload = encode(V1, {1: "SKU", 2: 1})
        malformed_body = bytearray(payload)
        malformed_body[HEADER.size + 3:HEADER.size + 7] = struct.pack("<I", 0xFFFFFFFF)
        with self.assertRaisesRegex(ProtocolError, "field length"):
            decode(bytes(malformed_body), V1, negotiated(V1, V1))

        bad_body_length = bytearray(payload)
        bad_body_length[8:12] = struct.pack("<I", len(payload))
        with self.assertRaisesRegex(ProtocolError, "body length"):
            decode(bytes(bad_body_length), V1, negotiated(V1, V1))

        with self.assertRaisesRegex(ProtocolError, "total length"):
            decode(b"\0" * (MAX_TOTAL_BYTES + 1), V1, negotiated(V1, V1))

    def test_known_type_and_payload_lengths_are_strict(self) -> None:
        base = encode(V1, {1: "SKU", 2: 1})
        bad_int = replace_field(base, 2, 2, b"\x01")
        with self.assertRaisesRegex(ProtocolError, "exactly four"):
            decode(bad_int, V1, negotiated(V1, V1))

        bad_utf8 = replace_field(base, 1, 1, b"\xff")
        with self.assertRaisesRegex(ProtocolError, "malformed UTF-8"):
            decode(bad_utf8, V1, negotiated(V1, V1))

    def test_negotiation_is_bound_to_local_manifest(self) -> None:
        session = negotiated(V1, V2)
        with self.assertRaisesRegex(ProtocolError, "does not belong"):
            decode(encode(V2, {1: "SKU", 2: 1}), V2, session)


if __name__ == "__main__":
    unittest.main(verbosity=2)
