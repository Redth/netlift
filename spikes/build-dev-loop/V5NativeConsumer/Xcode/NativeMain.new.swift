import Foundation
import NetLiftV5Host

@main
struct ProjectionHostV5Main {
    static func encodeUsingIssuedPeerContext(
        _ context: NetLiftV5PeerContext
    ) throws -> [UInt8] {
        try NetLiftV5DtoCodec.encodePayload(
            Payload(
                sku: "SKU-NEW",
                quantity: 42,
                note: "",
                nullableNote: nil,
                giftWrap: false),
            context: context)
    }

    static func main() {
        precondition(NetLiftV5DtoCodec.schemaHash ==
            "9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245")
        print("NATIVE_V5_CODEC_TYPECHECK_PASS direction=new native=\(nativeOnlyMarker())")
    }
}
