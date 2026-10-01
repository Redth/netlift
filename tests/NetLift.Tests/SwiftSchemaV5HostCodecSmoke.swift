import Foundation
import NetLiftV5Host

private enum SmokeFailure: Error {
    case assertion(String)
}

@main
private struct SwiftSchemaV5HostCodecSmoke {
    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            throw SmokeFailure.assertion("Expected the schema-v5 host-codec fixture directory.")
        }
        let fixtures = CommandLine.arguments[1]
        let oldPayload = try hostCodecBytes("old-payload.bin", in: fixtures)
        let newPayload = try hostCodecBytes("new-payload.bin", in: fixtures)
#if SCHEMA_V5_HOST_OLD
        let contextName = "new-to-old-context.txt"
        let mismatchedContextName = "old-to-new-context.txt"
        let context = try NetLiftV5PeerContext.testingOnly(
            contentsOf: URL(fileURLWithPath: fixtures).appendingPathComponent(contextName).path)
        let encodedOld = try NetLiftV5DtoCodec.encodePayload(
            Payload(sku: "SKU-OLD", quantity: 19), context: context)
        precondition(encodedOld == oldPayload)
        precondition(hostCodecFieldIDs(encodedOld) == [1, 2])
#else
        let contextName = "old-to-new-context.txt"
        let mismatchedContextName = "new-to-old-context.txt"
        let context = try NetLiftV5PeerContext.testingOnly(
            contentsOf: URL(fileURLWithPath: fixtures).appendingPathComponent(contextName).path)
        let encodedNew = try NetLiftV5DtoCodec.encodePayload(
            Payload(sku: "SKU-NEW", quantity: 42, note: "", nullableNote: nil, giftWrap: false),
            context: context)
        precondition(encodedNew == newPayload)
        precondition(hostCodecFieldIDs(encodedNew) == [1, 2, 7, 8, 9])
        let emptyOptional = try NetLiftV5DtoCodec.encodePayload(
            Payload(sku: "SKU-EMPTY", quantity: 1, note: "", nullableNote: "", giftWrap: false),
            context: context)
        precondition(emptyOptional != newPayload, "Empty nullable UTF-8 must remain distinct from null.")
#endif
        precondition(context.localSchemaHash != NetLiftV5DtoCodec.schemaHash,
            "The fixture must distinguish the managed-local and native-peer hashes.")
        try requireHostCodecRejects {
            _ = try NetLiftV5DtoCodec.encodePayload(
                hostTestPayload("SKU", oversized: true),
                context: context)
        }
        let mismatchedContext = try NetLiftV5PeerContext.testingOnly(
            contentsOf: URL(fileURLWithPath: fixtures).appendingPathComponent(mismatchedContextName).path)
        try requireHostCodecRejects {
            _ = try NetLiftV5DtoCodec.encodePayload(
                hostTestPayload("SKU"),
                context: mismatchedContext)
        }
        let mutations: [NetLiftV5PeerContextTestMutation] = [
            .inactive, .invalidLocalHash, .wrongAbi, .wrongSchemaVersion, .wrongContractMajor,
            .wrongMinor, .unsupportedFeature, .unsupportedRequiredFeature,
            .wrongService, .wrongMethod, .wrongDto,
            .wrongDtoVersion, .wrongPeerHash, .invalidPeerHash, .unknownRequiredField,
            .incompatibleKind, .incompatibleNullability, .incompatibleOptionality, .duplicateField
        ]
        for mutation in mutations {
            let invalidContext = try NetLiftV5PeerContext.testingOnly(
                contentsOf: URL(fileURLWithPath: fixtures).appendingPathComponent(contextName).path,
                mutation: mutation)
            try requireHostCodecRejects {
                _ = try NetLiftV5DtoCodec.encodePayload(
                    hostTestPayload("SKU"),
                    context: invalidContext)
            }
        }
        print("PASS Swift host-bound v5 outbound codecs: both role mappings, peer-hash binding, profile/field rejection, and payload bounds.")
    }
}

private func requireHostCodecRejects(_ action: () throws -> Void) throws {
    do {
        try action()
    } catch {
        return
    }
    throw SmokeFailure.assertion("Expected the host-bound schema-v5 codec to reject invalid input.")
}

private func hostCodecBytes(_ name: String, in directory: String) throws -> [UInt8] {
    Array(try Data(contentsOf: URL(fileURLWithPath: directory).appendingPathComponent(name)))
}

#if SCHEMA_V5_HOST_OLD
private func hostTestPayload(_ sku: String, oversized: Bool = false) -> Payload {
    Payload(sku: oversized ? String(repeating: "x", count: 17000) : sku, quantity: 1)
}
#else
private func hostTestPayload(_ sku: String, oversized: Bool = false) -> Payload {
    Payload(
        sku: sku,
        quantity: 1,
        note: oversized ? String(repeating: "x", count: 17000) : "",
        nullableNote: nil,
        giftWrap: false)
}
#endif

private func hostCodecReadU32(_ bytes: [UInt8], _ offset: Int) -> UInt32 {
    (0..<4).reduce(UInt32(0)) {
        $0 | (UInt32(bytes[offset + $1]) << UInt32($1 * 8))
    }
}

private func hostCodecFieldIDs(_ bytes: [UInt8]) -> [UInt16] {
    guard bytes.count >= 12 else { return [] }
    var ids = [UInt16]()
    var offset = 12
    while offset < bytes.count {
        guard bytes.count - offset >= 7 else { return [] }
        let id = UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
        let length = Int(hostCodecReadU32(bytes, offset + 3))
        guard length <= bytes.count - offset - 7 else { return [] }
        ids.append(id)
        offset += 7 + length
    }
    return ids
}
