import Foundation

private enum SmokeFailure: Error {
    case assertion(String)
}

@main
private struct SwiftSchemaV5NewCodecSmoke {
    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            throw SmokeFailure.assertion("Expected the schema-v5 fixture directory.")
        }
        let fixtures = CommandLine.arguments[1]
        let peerOld = try schemaV5Peer(URL(fileURLWithPath: fixtures)
            .appendingPathComponent("peer-old.txt").path)
        let peerNew = try schemaV5Peer(URL(fileURLWithPath: fixtures)
            .appendingPathComponent("peer-new.txt").path)
        let oldPayload = try schemaV5Bytes("old-payload.bin", in: fixtures)
        let newPayload = try schemaV5Bytes("new-payload.bin", in: fixtures)

        let decodedOld = try NetLiftV5DtoCodec.decodePayload(oldPayload, peer: peerOld)
        precondition(decodedOld.sku == "SKU-OLD" && decodedOld.quantity == 19)
        precondition(decodedOld.note == "")
        precondition(decodedOld.nullableNote == nil)
        precondition(!decodedOld.giftWrap)

        let decodedNew = try NetLiftV5DtoCodec.decodePayload(newPayload, peer: peerNew)
        precondition(decodedNew.note == "" && decodedNew.nullableNote == nil && !decodedNew.giftWrap)
        let encodedNew = try NetLiftV5DtoCodec.encodePayload(Payload(
            sku: "SKU-NEW",
            quantity: 42,
            note: "",
            nullableNote: nil,
            giftWrap: false))
        precondition(encodedNew == newPayload, "Swift new-schema NLF1 bytes differ from managed preflight bytes.")

        let emptyOptionalString = try NetLiftV5DtoCodec.decodePayload(
            NetLiftV5DtoCodec.encodePayload(Payload(
                sku: "SKU-EMPTY",
                quantity: 1,
                note: "",
                nullableNote: "",
                giftWrap: false)),
            peer: peerNew)
        precondition(emptyOptionalString.nullableNote == "")

        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5ReplaceField(newPayload, id: 1, kind: 0, payload: []),
                peer: peerNew)
        }
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5ReplaceField(newPayload, id: 1, kind: 1, payload: [0xff]),
                peer: peerNew)
        }
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                schemaV5ReplaceField(newPayload, id: 9, kind: 3, payload: [2]),
                peer: peerNew)
        }
        try expectSchemaV5Failure(.malformed) {
            _ = try NetLiftV5DtoCodec.decodePayload(
                Array(newPayload.dropLast()), peer: peerNew)
        }
        try expectSchemaV5Failure(.bufferTooLarge) {
            _ = try NetLiftV5DtoCodec.encodePayload(Payload(
                sku: "SKU",
                quantity: 1,
                note: String(repeating: "x", count: 17000),
                nullableNote: nil,
                giftWrap: false))
        }

        print("PASS Swift schema-v5 new DTO defaults, null/empty distinction, codec parity, and malformed NLF1 rejection.")
    }
}
