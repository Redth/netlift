# Additive optional-field negotiation spike

**Status:** isolated executable wire/handshake proof, verified 2026-09-25.
It changes no shared schema, generator, ABI, runtime host, or mobile path.
All experiment inputs and code live in `spikes/optional-negotiation/`.

## Independently specified manifests

The two JSON manifests are separate, checked-in fixtures, not one schema
generated from the other:

| Manifest | ABI/schema version | SHA-256 of canonical `schema` JSON |
| --- | --- | --- |
| `v1.schema.json` | ABI 1, schema 1.0 | `e8a8ddadd2780c938b47dad4771a75942bbb6614404841b56b3dedcf1fd0a74f` |
| `v2.schema.json` | ABI 1, schema 1.1 | `ae6e29945495b3982664b4fb07365c2ef6a4d50ec94edd35e92a276c15c11ef4` |

Both define service 17 and DTO `OrderDraft`. IDs 1 (`sku`, UTF-8) and 2
(`quantity`, signed little-endian i32) are required and unchanged. v2 adds
ID 7 (`note`, UTF-8, optional, default empty string) and ID 9 (`gift_wrap`,
bool, optional, default false). Hashing sorts JSON object keys, emits
compact UTF-8 JSON, and hashes only the `schema` object; the envelope's
`schemaHash` is not self-included. Load verifies each pinned hash before it
can participate in negotiation.

## Executable compatibility rule

The handshake checks ABI major, schema major, service ID, DTO name, and
field contracts. Equal, validated hashes use strict mode and need no
additive feature opt-in. Different hashes are accepted only when both peers
opt in, both advertise `FEATURE_ADDITIVE_OPTIONAL_FIELDS`, and one schema
minor is strictly newer. Compatibility proof requires every existing field
ID to remain present with the same name, type, nullability, optional bit and
default. Every added field must be optional, use a supported type, and
declare a type-correct default. Removal/renumbering, mandatory addition,
type/nullability/default change, unsupported defaults/types, schema-major
mismatch, or invalid manifest hash rejects the handshake.

Only the successful proof produces a `Negotiated` context. A receiver using
v1 gets a skip set containing only IDs 7 and 9 from the hash-validated v2
peer manifest. Decode rechecks the peer hash and compatibility proof before
using that set. It skips an unknown field only if the ID is present in that
validated peer manifest, the peer marks it optional, the ID is in the
proven additive set, and the bytes have the peer-declared wire type and a
valid bounded encoding. Unknown IDs absent from the peer manifest,
mandatory peer fields, and mismatched field encodings fail. Duplicate IDs
fail, including duplicate known fields. A v2 receiver decoding a v1 payload
fills absent IDs 7 and 9 from their validated defaults.

If either side opts out, a hash difference is rejected; it does not fall
back to a permissive decoder. An exact-hash handshake remains available
without feature negotiation.

## Wire slice and bounds

The payload has a 12-byte header (`<4sHHI`: magic `NLF1`, wire version,
field count, body byte length), followed by ID-keyed records with a 7-byte
header (`<HBI`: uint16 field ID, uint8 type, uint32 payload byte length)
and raw bytes. Fields are length-delimited, little-endian, and sorted by ID
when encoded. Supported values are strict UTF-8, signed i32, bool, and an
explicit zero-length null tag only for nullable fields. Empty string and
null remain distinct; text is not normalized and decoding rejects malformed
UTF-8. The current fixture fields are non-nullable.

The implementation limits a manifest to 16 KiB, a DTO to 64 fields, a
payload to 64 KiB, and one field payload to 16 KiB. It rejects truncation,
oversized lengths, body/count mismatch, trailing bytes, unsupported wire
types, malformed fixed-width values, and invalid total sizes before
decoding. There are no numeric or string coercions.

## Reproduce and results

From the workspace root:

```sh
bash spikes/optional-negotiation/run.sh
```

The runner uses Python `unittest` and the standard library only; no package
installation or network access is required. Verified with Python 3.14.6.
The exact result was:

```text
Ran 26 tests in 0.003s
OK
```

The 26 cases cover pinned distinct hashes; exact-hash strict mode; opt-out
and missing-peer-feature rejection; strict boolean opt-in; additive proof;
old-to-new decoding with defaults; new-to-old decoding and cross-version
round trip; null-versus-empty string; unknown fields absent from the peer
manifest (including a locally known field); wrong peer-declared wire type;
mandatory additions at handshake and defensive
decoder validation; peer/hash mismatch and modified manifest content;
renumber/removal, type and nullability incompatibility;
unsupported type/default; duplicate manifest keys and duplicate known wire
IDs; malformed/truncated lengths, trailing bytes and total-size bounds;
strict UTF-8/i32 lengths; and decoder use with the wrong local manifest.

## Native-owned C to .NET 11 CoreCLR host extension

The `managed/` extension independently implements the manifest parser,
canonical hash verification, bilateral version/feature handshake, additive
proof, and ID-keyed decoder in a package-free .NET 11 class library. The
native C executable loads CoreCLR through the installed `nethost`/`hostfxr`
pack and invokes `[UnmanagedCallersOnly]` Cdecl entrypoints. It passes the
v1/v2 JSON bytes and the separately advertised peer hash across the native
boundary; managed code recomputes SHA-256 from canonical schema JSON before
it stores an opaque, process-local negotiation session. Native C constructs
the ID-keyed payloads and verifies decoded fields/defaults through a
versioned experiment-local output struct.

The source `v1.schema.json` and `v2.schema.json` remain separate fixtures
with the distinct pinned hashes above. The managed side validates both
hashes itself. `prepare_negative_manifests.py` derives hash-valid negative
fixtures for mandatory additions, unsupported defaults, same-minor changes,
major-version mismatches, renumbering, and existing-field type/nullability
changes; this lets the native host test compatibility rejection rather than
merely failing on a stale hash.

Run from the workspace root:

```sh
bash spikes/optional-negotiation/run-managed.sh
```

This builds the managed library, creates the negative fixtures using Python's
standard library, compiles the C host against the installed .NET host pack,
starts CoreCLR, and runs the native assertions. It requires .NET 11, Python
3, and Apple Clang on macOS; it does not restore NuGet packages or access the
network. Verified with .NET SDK/runtime `11.0.100-rc.1.26425.128` and Apple
clang `21.0.0 (clang-2100.3.34.2)`. The .NET 11 build completed with 0
warnings and 0 errors; the native executable reported 43 `PASS` assertions,
including:

```text
PASS bilateral additive handshake old to new status=0
PASS bilateral additive handshake new to old status=0
PASS unknown optional fields skipped from validated V2 peer
PASS missing V2 optionals populated from validated defaults
PASS exact-hash strict fallback without opt-in status=0
PASS strict exact-hash mode rejects unknown field status=10
PASS local opt-out blocks downgrade status=6
PASS peer opt-out blocks downgrade status=6
PASS peer hash claim mismatch status=5
PASS mutated manifest content hash mismatch status=5
PASS mandatory field addition rejected status=8
PASS unsupported default rejected status=4
PASS ABI major mismatch rejected status=8
PASS schema major mismatch rejected status=8
PASS renumbered field rejected status=8
PASS existing type change rejected status=8
PASS existing nullability change rejected status=8
PASS V1 peer cannot authorize V2-only ID 7 status=10
PASS optional skip rejects peer wire type mismatch status=10
PASS optional skip validates UTF-8 wire shape status=10
PASS field maximum-size bound status=10
PASS native C/CoreCLR optional negotiation prototype
```

The managed/native runner passed 10 additional consecutive executions with
the same 43 assertions each time. The standard-library Python reference
runner passed 26 tests (`Ran 26 tests ... / OK`).

The C host also checks both negotiation result struct size/version guards,
both exact old/new decode directions, opt-in value strictness, bilateral
feature rejection, manifest and payload length limits, unknown IDs,
duplicate known IDs, malformed bool/UTF-8, truncated headers, mismatched
body lengths, and oversized field lengths.

## Limitations

This is an executable reference and native-owned CoreCLR host proof, not
production generator output or a shared ABI implementation. The managed C
projection is intentionally specific to the `OrderDraft` fixture, and its
process-local session table has no production lifecycle or authentication
policy. The SHA-256 checks detect inconsistency but do not sign or
authenticate manifests; deployment still needs a trusted source for peer
manifests. The proof does not establish how the real generator emits
manifests, how product runtimes distribute or authenticate them, or how
negotiation behaves under product lifecycle/concurrency constraints. It
does not establish compatibility with v4 streams or integration with the
shipping ABI, Swift, Kotlin/JNI, mobile apps, or a product runtime
negotiation layer. Hash canonicalization and wire encoding are spike-local
choices and must not be assumed compatible with the existing prototype.

## Production integration checkpoint — design only, after v4

**Proposed boundary for review:** keep current schema-v2/v3/v4 generation,
runtime handshakes, C layouts and byte-for-byte goldens frozen. Add a new
schema-v5 opt-in that carries explicit optional/default field metadata and
the additive-negotiation protocol. Use the next audited feature bit
(`0x10` candidate) and a separately versioned compatible-start/negotiation
entrypoint; do not change the existing exact-hash `lift_start` path or append
fields to its v1 structs. No production implementation is included here.

The current generator serializes v2, typed-reply v3 and stream/event v4 as
distinct schema shapes in `LiftContractGenerator`; the v3 `replyProtocol` and
v4 `subscriptions` metadata must remain present when v5 opts into optional
fields. Schema v5 should be a superset of the v4-capable IR, not a replacement
for typed replies or streams. Contracts without explicit opt-in continue to
emit their existing version and artifacts. An opt-in contract that has no
stream or typed-reply feature must still use the same v5 envelope and
negotiation rules, while unrelated exact-hash contracts remain untouched.

### Contract revision and opt-in

Keep the canonical serializer discriminator `schemaVersion: 5` distinct
from contract evolution. Add a required top-level
`contractRevision: { major, minor }` to every opted-in v5 manifest. These
uint32 values are monotonic for one generated application contract: `major`
marks an incompatible contract family, and `minor` advances only for the
additive optional-DTO-field evolution supported by this first protocol.
`contractRevision` is part of the canonical schema, so both values affect
the full `schemaHash`; signatures and build-embedded trust records bind the
same canonical bytes. It is not inferred from `schemaVersion`, service
versions, DTO versions, assembly versions, or package versions.

One contract means the complete schema emitted for a managed compilation
and its generated native facade. The proposed explicit source opt-in is a
new assembly-level declaration such as
`[assembly: LiftContractEvolution(Major = 1, Minor = 0, Mode = AdditiveOptionalV1)]`.
Without that declaration, generation remains byte-identical v2/v3/v4.
With it, the generator emits the v5 superset and explicit optional metadata;
the projection embeds that exact manifest. At runtime, each side must also
set the compatible-start opt-in bit—manifest capability alone does not
authorize negotiation. Existing `LiftService.Version` and `LiftDto.Version`
remain hash-covered identities and must remain unchanged across a compatible
minor; they are not substitutes for the top-level revision.

An exact full-hash match enters strict mode. Different full hashes with the
same `contractRevision` are rejected as inconsistent artifacts. Additive
mode requires the same contract major and unequal minors. Orient the pair
by revision, independent of which side initiates: prove that the higher
minor is exactly the lower-minor contract plus optional DTO-field additions.
Both connection directions may then negotiate if each side's own local
minimum-release policy accepts the peer revision. That policy is checked
independently on each side and is never lowered by the handshake. An older
local decoder may skip only the newer peer's proven additions; a newer local
decoder receiving an older peer payload applies its own defaults for omitted
optional fields. A payload claiming the older peer manifest cannot carry
newer-only IDs. Equal minor with different hashes, major mismatch, rollback
below either side's local floor, or a non-additive delta fails closed.
Across compatible minors, service, DTO, method and subscription sets must
be identical by stable ID and name. Service kind/version; method kind,
cancellation, parameter and return types/nullability; DTO identity/version;
and stream/event kind, parameters, item type and cancellation metadata must
also match exactly. The only permitted delta is adding optional fields to an
existing DTO. No service, method, DTO, stream or event additions/removals,
renumbering or semantic changes are negotiated by this protocol.

### Hashes, trusted manifests, and handshake

Retain `schemaHash` as the SHA-256 of the **complete canonical schema**,
including every optional marker and default. The handshake carries both
parties' full hashes; exact-hash v2/v3/v4 operation remains the existing
strict path. Do not introduce a “compatible hash” as a substitute for the
full hashes or for proving the field delta. If a compatible-base digest is
later added for cache lookup, it is only a hint: the full peer manifest must
still be authenticated, hash-checked and structurally compared before a
decoder is enabled.

For native and managed artifacts built together, prefer build-time embedding
of each side's canonical manifest in generated outputs. The managed compiler
and projection task must fail the build if embedded bytes or declared hashes
disagree. For separately distributed components, require a signed manifest
or a manifest index authenticated by an app-owned trust root; a peer's
self-reported SHA-256 is integrity evidence, not authentication. Verify the
signature/trust chain before parsing or negotiating, then bind the verified
manifest hash to the session. The signed identity must bind the full hash,
ABI/schema version, service identity and advertised feature set. For dynamic
components, the app-owned policy must also enforce a minimum release/schema
epoch or hash allowlist and key revocation/rotation rules; a valid signature
on an old manifest is not permission to roll back. The minimum is local
policy, never a peer-supplied handshake field. Never fetch an unauthenticated
manifest at runtime or treat a wire hash claim as proof of peer identity.

Compatible start requires ABI major 1, schema v5 on both sides, matching
contract major/service/DTO identity, explicit additive opt-in on both sides,
and the additive feature bit on both sides. The full peer hash must match
its validated manifest. Order the pair by contract minor rather than caller
role; accept only when the higher revision adds DTO fields marked optional
with supported, type-correct defaults to the lower revision. This permits
newer-local/older-peer decoding with local defaults while still rejecting
newer-only IDs in a payload attributed to the older peer. Reject removals,
renumbering, reused IDs,
changes to existing names/types/nullability/optionality/defaults, required
field additions, unknown schema data, major mismatches and unsupported
features. This first production mode should **not** implicitly negotiate
service/method additions or changes to stream/terminal semantics.

Generate an immutable negotiated context containing local and peer full
hashes plus the exact per-DTO added-field skip sets. Before enabling calls,
the generated decoder revalidates the context against its embedded manifest
and negotiation result. For each wire field, require membership in the
validated peer manifest before considering whether the local decoder knows
the ID. Decode common IDs only when their contracts match; skip an unknown
local ID only when the peer declares that exact ID optional, the negotiated
proof authorized it, and the bytes match its declared wire kind, nullability
and bounds. Reject unknown IDs absent from the peer manifest, duplicate IDs,
invalid lengths, unsupported values, and malformed encodings. Missing local
optional fields receive the local manifest's explicit default. Every
incompatibility or trust failure returns a typed initialization error and
leaves the host unavailable; there is no fallback to permissive decode.

### DTO semantics and v4 coexistence

Keep **optional/presence** distinct from **nullable/value**. An omitted
optional field receives its declared default; an explicitly transmitted
`null` is a value only when `nullable=true`. Empty string, zero, false, null
and omission remain distinct. Require a source-level explicit optional flag
and typed compile-time default; represent a default in v5 as a tagged
canonical value (for example `{"kind":"string","value":""}` or
`{"kind":"null"}`), and record explicit-default presence independently of
its value. Do not infer optionality from nullable annotations or CLR
constructor defaults. Initially accept only deterministic scalar defaults
supported by the wire codec (booleans, fixed-width integers, and strict
UTF-8 strings, plus null only for nullable fields); reject floating-point,
byte-array, nested DTO or collection defaults until their canonical
representation is specified. The analyzer must verify that the record
constructor and generated materializer preserve these semantics without
coercion.

For v5 contracts containing v4-style streams, apply the same negotiated DTO
field table to declared DTO `itemType` payloads only. Subscription IDs, item
type identity, framing,
credit, ordering, overflow, terminal status, cancellation and callback
lifetimes stay exact and unchanged. Typed native replies retain their
existing result/cancel/error outcomes; optional DTO defaults must never
turn a malformed reply or terminal error into a successful result. When a
contract combines v4 streams or typed replies with v5 negotiation, the
handshake must require every independently used feature bit (`0x8` stream,
`0x4` typed reply, and the proposed `0x10` additive bit) on both sides.
Missing one bit rejects that operation surface; schema v5 alone does not
enable it. v2/v3/v4 peers without the new opt-in remain exact-hash only and
cannot interoperate with a different v5 hash through a downgrade fallback.

### Ownership and advisory diff

The **managed compiler** owns field-attribute validation, the canonical v5
IR/manifest and full hash, compatibility-proof rules, generated managed
materialization/decoders, and a single deterministic test-vector suite. The
projection/build tooling emits C, Swift and Kotlin declarations and embeds
the same trusted manifest; it must not independently infer defaults or
reimplement compatibility policy. Apple SwiftPM/Xcode and Android
Gradle/packaging integrations own only per-target staging, embedding or
signature verification, feature opt-in configuration, and surfacing
initialization failures. They must not mutate schema bytes, create a second
schema hash, or silently enable additive mode. The runtime ABI owns the
versioned compatible-start entrypoint and typed rejection statuses; native
facades copy/route generated DTO values using the negotiated context.

`SchemaDiff` remains an advisory tool, not a runtime authorization. Extend
it to parse v5 only after the canonical shape is fixed. It may classify a
new explicit optional field with a supported default as `compatible` within
the same major; required additions and removals/renumbering, type,
nullability, existing default or requiredness changes remain `breaking`;
unknown v5 metadata remains `unsupported`. Reports must separately state
that compatibility was structurally classified but not negotiated. Preserve
the current v2 fixtures and outputs, and add explicit v3/v4/v5 self-diff and
cross-version fixtures rather than relabeling old schemas as v5.

### Required test matrix

| Area | Positive and negative cases |
| --- | --- |
| Frozen compatibility | Byte-identical v2/v3/v4 generated C#/C/Swift/Kotlin goldens and existing exact-hash host tests; no additive bit on default contracts. |
| Trust/hash | Embedded manifest/build mismatch; bad signature, wrong signer scope, revoked key or trust root; stale signed release/schema epoch below local minimum; wrong peer hash claim; modified schema under old hash; duplicate JSON keys; oversize manifest; unsupported feature bits. All fail before host readiness. |
| Negotiation/downgrade | Exact hash without opt-in stays strict; one-sided opt-out/feature fails; bilateral v5 opt-in succeeds for either initiator when the unordered pair proves a contract-minor optional addition and both local floors allow the peer; older-local decoding skips only proven additions, newer-local decoding applies defaults to older payloads; same-minor hash mismatch, non-additive changes (removal, type change, required addition), major/ABI mismatch, rollback below either local floor and a v4 peer with a nonmatching hash fail closed. |
| Schema evolution | Optional additions with valid string/bool/integer defaults; missing-field defaulting; reject required addition, removal, renumbering/reuse, existing type/name/nullability/optional/default changes, invalid default type/range and unsupported default kind. |
| Decoder trust/bounds | Unknown ID absent from peer (including an ID known locally); peer-declared optional with wrong type/null/bounds; duplicate known/unknown ID; malformed UTF-8/fixed-width value; truncation, count/body mismatch, trailing bytes and maximum-size boundaries. |
| Typed replies and streams | v5 typed result/decline/cancel/native-error behavior is unchanged; v5 contracts containing v4-style DTO stream items exercise defaults and authorized skips while item IDs, credit, overflow, terminal and release tests remain unchanged; non-DTO item or unsupported mixed feature rejects explicitly. |
| Tooling/targets | Managed generated decoder and native C/CoreCLR host run the same fixtures; C/Swift/Kotlin projections compile from one IR; Apple and Android build tasks embed identical manifest bytes; each native facade tests exact, negotiated and rejected startup. Device/package tests remain separate acceptance gates. |

### Narrow staged implementation plan

1. **Freeze and specify:** capture existing v2/v3/v4 golden hashes, ABI layouts
   and exact-host outputs; finalize v5 canonical field/default representation,
   source opt-in, trust mode and status mapping without touching existing
   generated output.
2. **Managed compiler and advisory:** add opt-in v5 IR/manifest and analyzer
   diagnostics, emit managed decode/materialization metadata, and extend
   `SchemaDiff` conservatively. Gate on unchanged v2/v3/v4 goldens plus
   schema/hash and evolution tests.
3. **Versioned runtime proof:** add a separate compatible-start ABI entrypoint
   and immutable negotiated context; exercise it with a native-owned C/CoreCLR
   host using embedded trusted manifests and the full negative matrix. Keep
   legacy `lift_start` exact-hash only.
4. **Generated facades and packaging:** emit native decoder metadata from the
   same IR, add build-time manifest consistency checks, then prove the
   separate opt-in path in Apple and Android build/package adapters. Do not
   promote it beyond experimental until both packaging paths and their
   lifecycle/device gates pass.

**Independent-review decision requested:** approve or revise this proposed
boundary before production code starts. This checkpoint adds no compiler,
ABI, package, mobile or test implementation.

Only `spikes/optional-negotiation/` and this report were changed.
