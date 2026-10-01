import Foundation
import NetLiftV5Host

@main
struct AsyncProjectionHostMain {
    static func encodeRequest(
        _ value: SubmitInput,
        context: NetLiftV5PeerContext
    ) throws -> [UInt8] {
        try NetLiftV5AsyncCodec.encodeRequest(value, context: context)
    }

    static func decodeResult(
        _ source: [UInt8],
        context: ManagedResultSchemaContext
    ) throws -> SubmitResult {
        try NetLiftV5AsyncCodec.decodeResult(source, context: context)
    }

    static func main() {}
}
