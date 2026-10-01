import Foundation
import NetLiftV5Host

@main
private enum AsyncHostCodecSmoke {
    static func main() throws {
        let fixtures = CommandLine.arguments[1]
#if SCHEMA_ASYNC_UTF8
        let resultContext = try ManagedResultSchemaContext.testingOnly(
            path: "\(fixtures)/utf8-result-context.txt")
        let validPayload = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/utf8-result.bin"))
        let decoded = try NetLiftV5AsyncCodec.decodeResult(Array(validPayload), context: resultContext)
        guard decoded.code == "utf8-ok", decoded.accepted, !decoded.audited else {
            fatalError("The async UTF-8 result did not round-trip with its optional default.")
        }
        let invalidPayload = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/utf8-invalid-result.bin"))
        expectReject {
            _ = try NetLiftV5AsyncCodec.decodeResult(Array(invalidPayload), context: resultContext)
        }
        print("PASS Swift async host codec strict UTF-8 result decoding.")
#elseif SCHEMA_ASYNC_NEW
        let requestContext = try NetLiftV5PeerContext.testingOnly(path: "\(fixtures)/new-request-context.txt")
        let request = SubmitInput(name: "SKU-NEW", count: 42, note: "note")
        let encoded = try NetLiftV5AsyncCodec.encodeRequest(request, context: requestContext)
        let expected = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/new-request.bin"))
        guard encoded == Array(expected), fieldIDs(encoded) == [1, 2, 7] else {
            fatalError("The new async request did not encode its optional addition deterministically.")
        }
        let defaultRequest = try NetLiftV5AsyncCodec.encodeRequest(
            SubmitInput(name: "SKU-DEFAULT", count: 1, note: ""),
            context: requestContext)
        let defaultExpected = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/new-request-default.bin"))
        guard defaultRequest == Array(defaultExpected), fieldIDs(defaultRequest) == [1, 2, 7] else {
            fatalError("The new request did not encode the declared optional default.")
        }
        let crossContext = try ManagedResultSchemaContext.testingOnly(
            path: "\(fixtures)/new-codec-old-managed-result-context.txt")
        let oldResult = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/old-result.bin"))
        let decodedOld = try NetLiftV5AsyncCodec.decodeResult(Array(oldResult), context: crossContext)
        guard decodedOld.code == 201, decodedOld.accepted, !decodedOld.audited else {
            fatalError("An old managed result did not supply the new native optional default.")
        }
        let ownContext = try ManagedResultSchemaContext.testingOnly(path: "\(fixtures)/new-result-context.txt")
        let newResult = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/new-result.bin"))
        let decodedNew = try NetLiftV5AsyncCodec.decodeResult(Array(newResult), context: ownContext)
        guard decodedNew.code == 202, decodedNew.accepted, decodedNew.audited else {
            fatalError("The new managed result did not decode its optional field.")
        }
        rejectInvalidContexts(
            request,
            fixtures: fixtures,
            requestContextFile: "new-request-context.txt",
            resultContextFile: "new-result-context.txt",
            resultPayload: Array(newResult))
        expectReject {
            _ = try NetLiftV5AsyncCodec.decodeResult(Array(repeating: 0, count: 4097), context: ownContext)
        }
        var malformed = Array(newResult)
        malformed[malformed.count - 1] = 2
        expectReject { _ = try NetLiftV5AsyncCodec.decodeResult(malformed, context: ownContext) }
        var duplicate = Array(newResult)
        duplicate[23] = 1
        duplicate[24] = 0
        expectReject { _ = try NetLiftV5AsyncCodec.decodeResult(duplicate, context: ownContext) }
        var badLength = Array(newResult)
        badLength[15] = 0xff
        expectReject { _ = try NetLiftV5AsyncCodec.decodeResult(badLength, context: ownContext) }
        expectReject {
            _ = try NetLiftV5AsyncCodec.encodeRequest(
                SubmitInput(name: String(repeating: "x", count: 4096), count: 1, note: ""),
                context: requestContext)
        }
        print("PASS Swift async host codec new caller with old/new result peers.")
#else
        let requestContext = try NetLiftV5PeerContext.testingOnly(path: "\(fixtures)/old-request-context.txt")
        let request = SubmitInput(name: "SKU-OLD", count: 19)
        let encoded = try NetLiftV5AsyncCodec.encodeRequest(request, context: requestContext)
        let expected = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/old-request.bin"))
        guard encoded == Array(expected), fieldIDs(encoded) == [1, 2] else {
            fatalError("The old async request did not encode the expected stable fields.")
        }
        let crossContext = try ManagedResultSchemaContext.testingOnly(
            path: "\(fixtures)/old-codec-new-managed-result-context.txt")
        let newResult = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/new-result.bin"))
        let decodedNew = try NetLiftV5AsyncCodec.decodeResult(Array(newResult), context: crossContext)
        guard decodedNew.code == 202, decodedNew.accepted else {
            fatalError("The old native codec did not decode common fields from the new result.");
        }
        let ownContext = try ManagedResultSchemaContext.testingOnly(path: "\(fixtures)/old-result-context.txt")
        let oldResult = try Data(contentsOf: URL(fileURLWithPath: "\(fixtures)/old-result.bin"))
        let decodedOld = try NetLiftV5AsyncCodec.decodeResult(Array(oldResult), context: ownContext)
        guard decodedOld.code == 201, decodedOld.accepted else {
            fatalError("The old managed result did not decode with its exact result descriptor.")
        }
        rejectInvalidContexts(
            request,
            fixtures: fixtures,
            requestContextFile: "old-request-context.txt",
            resultContextFile: "old-codec-new-managed-result-context.txt",
            resultPayload: Array(newResult))
        print("PASS Swift async host codec old caller with new/old result peers.")
#endif
    }

    private static func rejectInvalidContexts(
        _ request: SubmitInput,
        fixtures: String,
        requestContextFile: String,
        resultContextFile: String,
        resultPayload: [UInt8]
    ) {
        let requestMutations: [AsyncHostContextTestMutation] = [
            .inactive, .invalidLocalHash, .wrongPeerHash, .wrongAbi, .wrongFeatures,
            .wrongContract, .wrongService, .wrongMethod, .wrongDto, .wrongDtoVersion,
            .unknownRequiredField, .incompatibleKind, .duplicateField, .missingRequiredField
        ]
        for mutation in requestMutations {
            let context = try! NetLiftV5PeerContext.testingOnly(
                path: "\(fixtures)/\(requestContextFile)", mutation: mutation)
            expectReject { _ = try NetLiftV5AsyncCodec.encodeRequest(request, context: context) }
        }
        let resultMutations: [AsyncHostContextTestMutation] = [
            .inactive, .invalidLocalHash, .wrongPeerHash, .wrongProfile, .wrongAbi,
            .wrongFeatures, .wrongContract, .wrongService, .wrongMethod, .wrongDto,
            .wrongDtoVersion, .unknownRequiredField, .incompatibleKind, .duplicateField,
            .missingRequiredField
        ]
        for mutation in resultMutations {
            let context = try! ManagedResultSchemaContext.testingOnly(
                path: "\(fixtures)/\(resultContextFile)", mutation: mutation)
            expectReject {
                _ = try NetLiftV5AsyncCodec.decodeResult(resultPayload, context: context)
            }
        }
    }

    private static func expectReject(_ action: () throws -> Void) {
        do {
            try action()
        } catch {
            return
        }
        fatalError("Expected async schema-v5 host codec rejection.")
    }

    private static func fieldIDs(_ bytes: [UInt8]) -> [UInt16] {
        guard bytes.count >= 12 else { return [] }
        var offset = 12
        var ids = [UInt16]()
        while offset < bytes.count {
            guard bytes.count - offset >= 7 else { return [] }
            let id = UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
            let length = (0..<4).reduce(UInt32(0)) {
                $0 | (UInt32(bytes[offset + 3 + $1]) << UInt32($1 * 8))
            }
            guard length <= bytes.count - offset - 7 else { return [] }
            ids.append(id)
            offset += 7 + Int(length)
        }
        return ids
    }
}
