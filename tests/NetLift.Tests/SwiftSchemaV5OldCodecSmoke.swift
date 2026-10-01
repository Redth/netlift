import Foundation

private enum SmokeFailure: Error {
    case assertion(String)
}

@main
private struct SwiftSchemaV5OldCodecSmoke {
    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            throw SmokeFailure.assertion("Expected the schema-v5 fixture directory.")
        }
        let fixtures = CommandLine.arguments[1]
        let peerNew = try schemaV5Peer(URL(fileURLWithPath: fixtures)
            .appendingPathComponent("peer-new.txt").path)
        let oldPayload = try schemaV5Bytes("old-payload.bin", in: fixtures)
        let newPayload = try schemaV5Bytes("new-payload.bin", in: fixtures)

        let decodedNew = try NetLiftV5DtoCodec.decodePayload(newPayload, peer: peerNew)
        precondition(decodedNew.sku == "SKU-NEW" && decodedNew.quantity == 42)
        precondition(NetLiftV5DtoCodec.canonicalJson.contains(NetLiftV5DtoCodec.schemaHash))
        let encodedOld = try NetLiftV5DtoCodec.encodePayload(Payload(sku: "SKU-OLD", quantity: 19))
        precondition(encodedOld == oldPayload, "Swift old-schema NLF1 bytes differ from managed preflight bytes.")

        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(Array(newPayload.prefix(9)), peer: peerNew)
        }
        try expectSchemaV5Failure(.bufferTooLarge) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                [UInt8](repeating: 0, count: 65537), peer: peerNew)
        }

        var wrongBodyLength = newPayload
        wrongBodyLength[8] ^= 1
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(wrongBodyLength, peer: peerNew)
        }
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5AppendField(oldPayload, id: 11, kind: 1, payload: [0x78]),
                peer: peerNew)
        }
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5AppendField(oldPayload, id: 1, kind: 1, payload: [0x78]),
                peer: peerNew)
        }
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5ReplaceField(newPayload, id: 7, kind: 1, payload: [0xff]),
                peer: peerNew)
        }
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5ReplaceField(newPayload, id: 9, kind: 3, payload: [2]),
                peer: peerNew)
        }

        let requiredPeer = NetLiftV5PeerSchema.testingOnly(
            schemaHash: peerNew.schemaHash,
            fields: peerNew.fields + [
                NetLiftV5PeerField(
                    testingOnly: (),
                    id: 11,
                    kind: .utf8,
                    nullable: false,
                    optional: false)
            ])
        try expectSchemaV5Failure(.invalidPeer) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5AppendField(oldPayload, id: 11, kind: 1, payload: [0x78]),
                peer: requiredPeer)
        }

        print("PASS Swift schema-v5 old DTO encoder/decoder, authorized optional skips, and malformed NLF1 rejection.")
    }
}
