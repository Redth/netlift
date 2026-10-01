# macOS Swift caller proof

This isolated proof is intended to exercise the generated schema-v5 Swift
codecs against the in-process CoreCLR old/new negotiation host. It is not an
iOS application or a signed-delivery proof.

## Host-owned peer context

The `NetLiftV5Host` module exposes this immutable value:

```swift
public struct NetLiftV5PeerContext: Sendable, Equatable {
    public let abiMajor: UInt32
    public let schemaVersion: UInt32
    public let contractMajor: UInt32
    public let contractMinor: UInt32
    public let featureBits: UInt32
    public let requiredFeatureBits: UInt32
    public let serviceID: UInt32
    public let methodID: UInt32
    public let dtoID: UInt32
    public let dtoVersion: UInt32
    public let localSchemaHash: String
    public let schemaHash: String
    public let fields: [NetLiftV5PeerField]
}

public struct NetLiftV5PeerField: Sendable, Equatable {
    public let id: UInt16
    public let kind: NetLiftV5PeerFieldKind
    public let nullable: Bool
    public let optional: Bool
}
```

Both types have non-public initializers. `NetLiftV5PeerContext` also carries
private endpoint identity/generation state; the public fields are an immutable
projection for codec validation, not a way to mint a context. A context is
issued only by `NetLiftV5Endpoint.peerContext()` after successful compatible
start and validation of that endpoint's NLPS v1 snapshot. `localSchemaHash` is
the managed endpoint's local manifest hash. The caller supplies the expected
managed schema hash to `start(expectedManagedSchemaHash:)`; the host compares
it against the managed local assembly's `GetManifest` result before issuing a
peer context. The generated peer codec binds to `schemaHash`, the negotiated
peer hash in the endpoint-bound NLPS v1 snapshot. The
public `requireActive() throws` checks a private endpoint lease, and dispatch
checks both that lease and the endpoint identity so a context cannot be reused
with another endpoint or after close.

Consumers can use `NetLiftV5Host.start(for:expectedManagedSchemaHash:)` for a
one-call start, or `makeEndpoint(for:)` followed by
`NetLiftV5Endpoint.start(expectedManagedSchemaHash:)` when they need to observe
pre-start/failure states.

The generated codec integration uses this host-issued context through a
generated API in a separate Swift module; production code must not call
`testingOnly` factories. The sync host-bound
`--emit-schema-v5-host-codecs` mode emits encode-only codecs whose schema hash
is the negotiated peer hash; existing codec-only v5 output and hashes remain
unchanged. The AsyncCallbackV1 host-bound
`--emit-schema-v5-async-host-codecs` mode emits request encoders and result
decoders; result decoding validates `ManagedResultSchemaContext` before reading
the payload.

## Proof cases

The default runner builds the real managed old/new assemblies and C/CoreCLR
reference proof in isolated artifacts, then opens CoreCLR in the Swift test
process. Swift tests validate pre-start and failed-start status, local-manifest
binding, both old/new NLPS profiles, endpoint-bound context use, dispatch
results/counters, and close invalidation. The default bridge-only mode retains
an intentionally test-local NLF1 encoder. Set
`NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1` to build the projection tool into
the selected artifact root, emit old/new host-bound Swift modules, and run the
same in-process CoreCLR test with those generated codecs.

The generated-codec run verifies both directions:

- Old managed endpoint + negotiated new peer: the generated new codec binds to
  the peer hash, emits fields `[1, 2, 7, 8, 9]`, and dispatches successfully.
  Passing the old codec against that context throws `invalidPeer` before any
  route call; counters are all zero until valid dispatch, then factory and
  route counts are exactly one.
- New managed endpoint + negotiated old peer: the generated old codec binds to
  the peer hash, emits fields `[1, 2]`, and dispatches successfully; the
  managed route confirms optional defaults. Passing the new codec against
  that context throws `invalidPeer` before any route call; counters are all
  zero until valid dispatch, then factory and route counts are exactly one.
- Encoding with the one-call endpoint's context after close throws
  `endpointClosed`.

Managed-owned snapshot buffers are copied and freed on success and every
failure path.

## Current implementation boundary

The host bridge, Swift context validator, and generated-codec consumer are
under this directory. The existing C reference runner, compiler-owned
NLPS/codec generator and outputs, Apple app sources, build-loop files, and
Android files are intentionally not modified. All build products use this directory's
isolated `native-artifacts/` output tree by default. A concurrent rerun can
select another isolated output root with
`NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/path/to/session-artifacts bash tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh`.
The sync generated-codec path is selected by setting
`NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1` alongside a fresh artifact root.
For AsyncCallbackV1, combine it with
`NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS=1`; the latter requires an explicit
artifact root.

## Verified generated-codec run

Command:

```sh
NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/code/netlift/tests/NetLift.Negotiation.NativeHost.SwiftCaller/native-artifacts-generated-codecs-final2 \
bash /Users/redth/code/netlift/tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
```

Result: C/CoreCLR old/new reference matrix PASS; SwiftPM generated-codec
integration PASS (1 test, 0 failures). A separate post-change default-mode
run also passed the C/CoreCLR matrix and bridge-only integration (1 test,
0 failures). The generated run is a macOS in-process CoreCLR proof; it is not
an iOS application, a simulator/device test, or signed-delivery evidence.

SHA-256 for the generated-codec run:

| Artifact | SHA-256 |
|---|---|
| Generated old codec `NetLift.swift` | `22f3a9a44bb3d3912f33f5dfc24c2fb5b9eb3286faf11bf21540debe9751e177` |
| Generated new codec `NetLift.swift` | `113113ad607aab8cfbc4b41071f808d9a81dce8e403b89c9820bcaa27a22f6d0` |
| Old managed assembly | `f3be37f4518cddd3b2ed8e732f877de85be8f6c5e16309d508a195ebd09a8db1` |
| New managed assembly | `a3d77b3d14a10eff5edb524bfaaf78ba9b19350e8dae9121be811a271e81e434` |
| Swift test executable | `4afa6f321206879cc3b5cd0318cd11e532d70fe3d5e727382dbe49499c76f4ce` |

This closes the generated host-codec caller gate for the scoped macOS spike.
The remaining limitations are iOS packaging/runtime validation, physical
device evidence, and signed delivery; none is claimed here.

## Verified AsyncCallbackV1 result-codec run

Command:

```sh
NETLIFT_V5_SWIFT_CALLER_ASYNC_RESULTS=1 \
NETLIFT_V5_SWIFT_CALLER_GENERATED_CODECS=1 \
NETLIFT_V5_SWIFT_CALLER_ARTIFACTS=/Users/redth/.copilot/session-state/1d2635bf-f83f-43e9-a1a8-b349fe90a1a8/files/nlmr-swiftcaller-attempt-04/async-final \
bash /Users/redth/code/netlift/tests/NetLift.Negotiation.NativeHost.SwiftCaller/run.sh
```

Result: both AsyncCallbackV1 managed wrappers and the projection tool built;
the SwiftPM live-CoreCLR NLMR/getter and generated `decodeResult` tests passed
(2 tests, 0 failures). The new codec decoded an old-managed result with its
optional `audited` default; the old codec decoded common fields from a
new-managed result carrying optional field 9. Wrong peer codecs were rejected,
malformed boolean payloads were rejected, failed-start/pre-start contexts were
unavailable, and decoding through a retained context after endpoint close
failed with `endpointClosed`. The decoded result payloads are deterministic
test vectors; AsyncCallbackV1 managed result dispatch/serialization and
callback execution are not wired into this caller proof.

The generated source hashes match the compiler-owned frozen outputs:

| Artifact | SHA-256 |
|---|---|
| Generated old async codec `NetLift.swift` | `10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d` |
| Generated new async codec `NetLift.swift` | `3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe` |
