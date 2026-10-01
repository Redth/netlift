import Foundation
import NetLiftV5Host

@main
struct ProjectionHostV5Main {
    static func encodeUsingIssuedPeerContext(
        _ context: NetLiftV5PeerContext
    ) throws -> [UInt8] {
        try NetLiftV5DtoCodec.encodePayload(
            Payload(sku: "SKU-OLD", quantity: 19),
            context: context)
    }

    static func main() {
        precondition(NetLiftV5DtoCodec.schemaHash ==
            "7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184")
        print("NATIVE_V5_CODEC_TYPECHECK_PASS direction=old native=\(nativeOnlyMarker())")
    }
}
