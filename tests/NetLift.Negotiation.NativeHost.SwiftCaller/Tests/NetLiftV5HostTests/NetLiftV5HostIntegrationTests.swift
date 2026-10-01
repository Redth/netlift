import Foundation
import XCTest
@testable import NetLiftV5Host
#if NETLIFT_V5_HOST_CODECS
import NetLiftV5OldCodec
import NetLiftV5NewCodec
#endif

final class NetLiftV5HostIntegrationTests: XCTestCase {
    private enum PayloadError: Error {
        case malformed
    }

    #if NETLIFT_V5_ASYNC_RESULTS
    private let oldSchemaHash =
        "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b"
    private let newSchemaHash =
        "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4"
    #else
    private let oldSchemaHash =
        "7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184"
    private let newSchemaHash =
        "9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245"
    #endif
    private let asyncOldSchemaHash =
        "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b"
    private let asyncNewSchemaHash =
        "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4"

    #if !NETLIFT_V5_ASYNC_RESULTS
    func testCoreCLRPeerContextAndEndpointLifecycle() throws {
        let host = try loadHost()

        let failedEndpoint = try host.makeEndpoint(for: .oldLocalWithNewPeer)
        XCTAssertThrowsError(
            try failedEndpoint.startWithWrongPinForTesting(expectedManagedSchemaHash: oldSchemaHash)) { error in
                XCTAssertEqual(
                    error as? NetLiftV5HostError,
                    .native(
                        status: 3,
                        message: "Peer manifest does not match the build-trusted schema hash."))
            }
        XCTAssertThrowsError(try failedEndpoint.peerContext()) { error in
            XCTAssertEqual(
                error as? NetLiftV5HostError,
                .native(status: 9, message: "Peer schema is unavailable until compatible-start succeeds."))
        }
        let failedCounters = try failedEndpoint.counters()
        XCTAssertEqual(failedCounters.factoryCalls, 0)
        XCTAssertEqual(failedCounters.routeCalls, 0)
        XCTAssertEqual(failedCounters.lastQuantity, 0)
        XCTAssertEqual(failedCounters.optionalDefaultsValid, 0)
        try failedEndpoint.close()

        let oldEndpoint = try host.makeEndpoint(for: .oldLocalWithNewPeer)
        XCTAssertThrowsError(try oldEndpoint.peerContext()) { error in
            XCTAssertEqual(
                error as? NetLiftV5HostError,
                .native(status: 9, message: "Peer schema is unavailable until compatible-start succeeds."))
        }
        XCTAssertThrowsError(
            try oldEndpoint.start(expectedManagedSchemaHash: String(repeating: "0", count: 64))) { error in
                XCTAssertEqual(
                    error as? NetLiftV5HostError,
                    .localManifestMismatch(
                        expected: String(repeating: "0", count: 64),
                        actual: self.oldSchemaHash))
            }

        try oldEndpoint.start(expectedManagedSchemaHash: oldSchemaHash)
        let oldContext = try oldEndpoint.peerContext()
        XCTAssertEqual(oldContext.localSchemaHash, oldSchemaHash)
        XCTAssertEqual(oldContext.schemaHash, newSchemaHash)
        XCTAssertEqual(oldContext.fields.map(\.id), [1, 2, 7, 8, 9])
        try oldContext.requireActive()

        let quantity = Int32(19)
        #if NETLIFT_V5_HOST_CODECS
        XCTAssertThrowsError(
            try NetLiftV5OldCodec.NetLiftV5DtoCodec.encodePayload(
                NetLiftV5OldCodec.Payload(sku: "WRONG-PEER", quantity: quantity),
                context: oldContext)) { error in
                    XCTAssertEqual(
                        error as? NetLiftV5OldCodec.NetLiftV5CodecError,
                        .invalidPeer)
                }
        let oldCountersBeforeDispatch = try oldEndpoint.counters()
        XCTAssertEqual(oldCountersBeforeDispatch.factoryCalls, 0)
        XCTAssertEqual(oldCountersBeforeDispatch.routeCalls, 0)
        XCTAssertEqual(oldCountersBeforeDispatch.lastQuantity, 0)
        XCTAssertEqual(oldCountersBeforeDispatch.optionalDefaultsValid, 0)

        let newDto = NetLiftV5NewCodec.Payload(
            sku: "SKU-NEW",
            quantity: quantity,
            note: "native new peer",
            nullableNote: nil,
            giftWrap: true)
        let newPayload = try NetLiftV5NewCodec.NetLiftV5DtoCodec.encodePayload(
            newDto,
            context: oldContext)
        XCTAssertEqual(try fieldIDs(in: newPayload), [1, 2, 7, 8, 9])
        #else
        let newPayload = payload(sku: "SKU-NEW", quantity: quantity)
        #endif
        XCTAssertEqual(try oldEndpoint.dispatch(newPayload, using: oldContext), quantity)
        let oldCounters = try oldEndpoint.counters()
        XCTAssertEqual(oldCounters.factoryCalls, 1)
        XCTAssertEqual(oldCounters.routeCalls, 1)
        XCTAssertEqual(oldCounters.lastQuantity, quantity)
        XCTAssertEqual(oldCounters.optionalDefaultsValid, 0)

        let newEndpoint = try host.makeEndpoint(for: .newLocalWithOldPeer)
        try newEndpoint.start(expectedManagedSchemaHash: newSchemaHash)
        let newContext = try newEndpoint.peerContext()
        XCTAssertEqual(newContext.localSchemaHash, newSchemaHash)
        XCTAssertEqual(newContext.schemaHash, oldSchemaHash)
        XCTAssertEqual(newContext.fields.map(\.id), [1, 2])

        let newQuantity = Int32(42)
        #if NETLIFT_V5_HOST_CODECS
        XCTAssertThrowsError(
            try NetLiftV5NewCodec.NetLiftV5DtoCodec.encodePayload(
                NetLiftV5NewCodec.Payload(
                    sku: "WRONG-PEER",
                    quantity: newQuantity,
                    note: "wrong peer",
                    nullableNote: nil,
                    giftWrap: true),
                context: newContext)) { error in
                    XCTAssertEqual(
                        error as? NetLiftV5NewCodec.NetLiftV5CodecError,
                        .invalidPeer)
                }
        let newCountersBeforeDispatch = try newEndpoint.counters()
        XCTAssertEqual(newCountersBeforeDispatch.factoryCalls, 0)
        XCTAssertEqual(newCountersBeforeDispatch.routeCalls, 0)
        XCTAssertEqual(newCountersBeforeDispatch.lastQuantity, 0)
        XCTAssertEqual(newCountersBeforeDispatch.optionalDefaultsValid, 0)

        let oldDto = NetLiftV5OldCodec.Payload(sku: "SKU-OLD", quantity: newQuantity)
        let oldPayload = try NetLiftV5OldCodec.NetLiftV5DtoCodec.encodePayload(
            oldDto,
            context: newContext)
        XCTAssertEqual(try fieldIDs(in: oldPayload), [1, 2])
        #else
        let oldPayload = payload(sku: "SKU-OLD", quantity: newQuantity)
        #endif
        XCTAssertEqual(try newEndpoint.dispatch(oldPayload, using: newContext), newQuantity)
        let newCounters = try newEndpoint.counters()
        XCTAssertEqual(newCounters.factoryCalls, 1)
        XCTAssertEqual(newCounters.routeCalls, 1)
        XCTAssertEqual(newCounters.lastQuantity, newQuantity)
        XCTAssertEqual(newCounters.optionalDefaultsValid, 1)

        XCTAssertThrowsError(try newEndpoint.dispatch(newPayload, using: oldContext)) { error in
            XCTAssertEqual(error as? NetLiftV5HostError, .contextBelongsToAnotherEndpoint)
        }

        try oldEndpoint.close()
        XCTAssertThrowsError(try oldContext.requireActive()) { error in
            XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
        }
        XCTAssertThrowsError(try oldEndpoint.dispatch(newPayload, using: oldContext)) { error in
            XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
        }
        XCTAssertThrowsError(try oldEndpoint.peerContext()) { error in
            XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
        }
        try newEndpoint.close()

        let convenienceEndpoint = try host.start(
            for: .newLocalWithOldPeer,
            expectedManagedSchemaHash: newSchemaHash)
        let convenienceContext = try convenienceEndpoint.peerContext()
        XCTAssertEqual(convenienceContext.localSchemaHash, newSchemaHash)
        XCTAssertEqual(convenienceContext.schemaHash, oldSchemaHash)
        try convenienceContext.requireActive()
        #if NETLIFT_V5_HOST_CODECS
        let convenienceDto = NetLiftV5OldCodec.Payload(sku: "SKU-BOUND", quantity: 7)
        let convenienceBytes = try NetLiftV5OldCodec.NetLiftV5DtoCodec.encodePayload(
            convenienceDto,
            context: convenienceContext)
        XCTAssertEqual(
            try convenienceEndpoint.dispatch(convenienceBytes, using: convenienceContext),
            7)
        #endif
        try convenienceEndpoint.close()
        XCTAssertThrowsError(try convenienceContext.requireActive()) { error in
            XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
        }
        #if NETLIFT_V5_HOST_CODECS
        XCTAssertThrowsError(
            try NetLiftV5OldCodec.NetLiftV5DtoCodec.encodePayload(
                NetLiftV5OldCodec.Payload(sku: "SKU-CLOSED", quantity: 1),
                context: convenienceContext)) { error in
                    XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
                }
        #endif
    }
    #endif

    func testManagedResultSchemaParserRejectsMalformedSnapshots() throws {
        let token: UInt64 = 0x0807060504030201
        let expectation = managedResultExpectation(endpointToken: token)
        let valid = managedResultDescriptor(endpointToken: token)
        let identity = UUID()
        let lifetime = NetLiftV5EndpointLifetime()
        let parsed = try ManagedResultSchemaParser.parse(
            Data(valid),
            expected: expectation,
            endpointIdentity: identity,
            lifetime: lifetime)

        XCTAssertEqual(parsed.localSchemaHash, asyncOldSchemaHash)
        XCTAssertEqual(parsed.peerSchemaHash, asyncNewSchemaHash)
        XCTAssertEqual(parsed.fields.map(\.id), [1, 2])
        XCTAssertEqual(parsed.fields.map(\.kind), [.i32, .bool])

        for length in 0..<136 {
            XCTAssertThrowsError(
                try ManagedResultSchemaParser.parse(
                    Data(valid.prefix(length)),
                    expected: expectation,
                    endpointIdentity: identity,
                    lifetime: lifetime),
                "accepted truncated NLMR length \(length)")
        }

        let mutations: [(String, (inout [UInt8]) -> Void)] = [
            ("bad magic", { $0[0] = 0 }),
            ("unsupported version", { self.setU16(2, at: 4, in: &$0) }),
            ("wrong header size", { self.setU16(135, at: 6, in: &$0) }),
            ("wrong total length", { self.setU32(143, at: 8, in: &$0) }),
            ("wrong direction", { self.setU32(2, at: 12, in: &$0) }),
            ("wrong endpoint token", { self.setU64(token + 1, at: 16, in: &$0) }),
            ("wrong profile", { self.setU32(2, at: 24, in: &$0) }),
            ("wrong ABI", { self.setU32(2, at: 28, in: &$0) }),
            ("wrong schema version", { self.setU32(6, at: 32, in: &$0) }),
            ("wrong contract minor", { self.setU32(1, at: 40, in: &$0) }),
            ("wrong feature bits", { self.setU32(0x35, at: 44, in: &$0) }),
            ("wrong required features", { self.setU32(0x35, at: 48, in: &$0) }),
            ("wrong service", { self.setU32(18, at: 52, in: &$0) }),
            ("wrong method", { self.setU32(2, at: 56, in: &$0) }),
            ("wrong result DTO", { self.setU32(23, at: 60, in: &$0) }),
            ("wrong result DTO version", { self.setU32(2, at: 64, in: &$0) }),
            ("wrong field count", { self.setU16(3, at: 68, in: &$0) }),
            ("wrong field record size", { self.setU16(8, at: 70, in: &$0) }),
            ("wrong local hash", { $0[72] ^= 1 }),
            ("wrong peer hash", { $0[104] ^= 1 }),
            ("swapped hashes", {
                let localHash = Array($0[72..<104])
                $0.replaceSubrange(72..<104, with: $0[104..<136])
                $0.replaceSubrange(104..<136, with: localHash)
            }),
            ("zero field ID", { self.setU16(0, at: 136, in: &$0) }),
            ("unsupported field kind", { $0[138] = 9 }),
            ("reserved field flag", { $0[139] |= 0x4 }),
            ("unexpected nullable flag", { $0[139] |= 0x1 }),
            ("duplicate or unsorted fields", { self.setU16(1, at: 140, in: &$0) }),
            ("wrong optional flag", { $0[139] |= 0x2 }),
            ("empty field table", {
                self.setU16(0, at: 68, in: &$0)
                self.setU32(136, at: 8, in: &$0)
                $0.removeLast(8)
            }),
        ]

        for (label, mutate) in mutations {
            var malformed = valid
            mutate(&malformed)
            XCTAssertThrowsError(
                try ManagedResultSchemaParser.parse(
                    Data(malformed),
                    expected: expectation,
                    endpointIdentity: identity,
                    lifetime: lifetime),
                "accepted \(label)")
        }

        var oversized = valid
        oversized.append(contentsOf: repeatElement(0, count: 393 - valid.count))
        XCTAssertThrowsError(
            try ManagedResultSchemaParser.parse(
                Data(oversized),
                expected: expectation,
                endpointIdentity: identity,
                lifetime: lifetime))
    }

    #if NETLIFT_V5_ASYNC_RESULTS
    func testCoreCLRManagedResultSchemasAreEndpointBoundAndLive() throws {
        let host = try loadHost()
        let failedEndpoint = try host.makeEndpoint(for: .oldLocalWithNewPeer)
        XCTAssertThrowsError(
            try failedEndpoint.startWithWrongPinForTesting(expectedManagedSchemaHash: oldSchemaHash))
        XCTAssertThrowsError(try failedEndpoint.managedResultSchemaContext()) { error in
            XCTAssertEqual(error as? NetLiftV5HostError, .endpointNotStarted)
        }
        try failedEndpoint.close()

        let cases: [(NetLiftV5Flow, String, String, [UInt16], UInt32)] = [
            (.oldLocalWithNewPeer, oldSchemaHash, newSchemaHash, [1, 2], 0),
            (.newLocalWithOldPeer, newSchemaHash, oldSchemaHash, [1, 2, 9], 1),
        ]

        for (flow, localHash, peerHash, resultFieldIDs, contractMinor) in cases {
            let endpoint = try host.makeEndpoint(for: flow)
            XCTAssertThrowsError(try endpoint.managedResultSchemaContext()) { error in
                XCTAssertEqual(error as? NetLiftV5HostError, .endpointNotStarted)
            }

            try endpoint.start(expectedManagedSchemaHash: localHash)
            let peer = try endpoint.peerContext()
            let result = try endpoint.managedResultSchemaContext()
            XCTAssertEqual(result.abiMajor, 1)
            XCTAssertEqual(result.schemaVersion, 5)
            XCTAssertEqual(result.contractMajor, 1)
            XCTAssertEqual(result.contractMinor, contractMinor)
            XCTAssertEqual(result.featureBits, 0x36)
            XCTAssertEqual(result.requiredFeatureBits, 0x36)
            XCTAssertEqual(result.profileId, 1)
            XCTAssertEqual(result.serviceID, peer.serviceID)
            XCTAssertEqual(result.methodID, peer.methodID)
            XCTAssertEqual(result.resultDTOID, 22)
            XCTAssertEqual(result.resultDTOVersion, 1)
            XCTAssertEqual(result.localSchemaHash, localHash)
            XCTAssertEqual(result.peerSchemaHash, peerHash)
            XCTAssertEqual(result.fields.map(\.id), resultFieldIDs)
            XCTAssertEqual(
                result.fields.map(\.kind),
                [.i32, .bool] + (resultFieldIDs.count == 3 ? [.bool] : []))
            XCTAssertEqual(
                result.fields.map(\.optional),
                resultFieldIDs.count == 3 ? [false, false, true] : [false, false])
            XCTAssertEqual(result.fields.map(\.nullable), [false, false] + (resultFieldIDs.count == 3 ? [false] : []))
            try result.requireActive()

            #if NETLIFT_V5_HOST_CODECS
            let resultPayload: [UInt8]
            switch flow {
            case .oldLocalWithNewPeer:
                resultPayload = asyncResultPayload(code: 201, accepted: true)
                let decoded = try NetLiftV5NewCodec.NetLiftV5AsyncCodec.decodeResult(
                    resultPayload,
                    context: result)
                XCTAssertEqual(decoded.code, 201)
                XCTAssertTrue(decoded.accepted)
                XCTAssertFalse(decoded.audited)
                XCTAssertThrowsError(
                    try NetLiftV5OldCodec.NetLiftV5AsyncCodec.decodeResult(
                        resultPayload,
                        context: result)) { error in
                            XCTAssertEqual(
                                error as? NetLiftV5OldCodec.NetLiftV5AsyncCodecError,
                                .invalidPeer)
                        }
            case .newLocalWithOldPeer:
                resultPayload = asyncResultPayload(code: 202, accepted: true, audited: true)
                let decoded = try NetLiftV5OldCodec.NetLiftV5AsyncCodec.decodeResult(
                    resultPayload,
                    context: result)
                XCTAssertEqual(decoded.code, 202)
                XCTAssertTrue(decoded.accepted)
                XCTAssertThrowsError(
                    try NetLiftV5NewCodec.NetLiftV5AsyncCodec.decodeResult(
                        resultPayload,
                        context: result)) { error in
                            XCTAssertEqual(
                                error as? NetLiftV5NewCodec.NetLiftV5AsyncCodecError,
                                .invalidPeer)
                        }
            }
            var malformedResultPayload = resultPayload
            malformedResultPayload[30] = 2
            XCTAssertThrowsError(
                try decodeAsyncResult(
                    malformedResultPayload,
                    flow: flow,
                    context: result))
            #endif

            try endpoint.close()
            XCTAssertThrowsError(try result.requireActive()) { error in
                XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
            }
#if NETLIFT_V5_HOST_CODECS
            XCTAssertThrowsError(
                try decodeAsyncResult(resultPayload, flow: flow, context: result)) { error in
                    XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
                }
#endif
            XCTAssertThrowsError(try endpoint.managedResultSchemaContext()) { error in
                XCTAssertEqual(error as? NetLiftV5HostError, .endpointClosed)
            }
        }
    }

    #if NETLIFT_V5_HOST_CODECS
    private func decodeAsyncResult(
        _ payload: [UInt8],
        flow: NetLiftV5Flow,
        context: ManagedResultSchemaContext
    ) throws {
        switch flow {
        case .oldLocalWithNewPeer:
            _ = try NetLiftV5NewCodec.NetLiftV5AsyncCodec.decodeResult(payload, context: context)
        case .newLocalWithOldPeer:
            _ = try NetLiftV5OldCodec.NetLiftV5AsyncCodec.decodeResult(payload, context: context)
        }
    }
    #endif
    #endif

    private func loadHost() throws -> NetLiftV5Host {
        let environment = ProcessInfo.processInfo.environment
        func path(_ key: String) throws -> URL {
            guard let value = environment[key], !value.isEmpty else {
                throw NSError(
                    domain: "NetLiftV5HostTests",
                    code: 1,
                    userInfo: [NSLocalizedDescriptionKey: "Missing environment variable \(key)."])
            }
            return URL(fileURLWithPath: value)
        }
        return try NetLiftV5Host.load(
            dotnetRoot: path("NETLIFT_V5_DOTNET_ROOT"),
            oldAssembly: path("NETLIFT_V5_OLD_ASSEMBLY"),
            oldRuntimeConfig: path("NETLIFT_V5_OLD_RUNTIMECONFIG"),
            newAssembly: path("NETLIFT_V5_NEW_ASSEMBLY"),
            newRuntimeConfig: path("NETLIFT_V5_NEW_RUNTIMECONFIG"))
    }

    private func payload(sku: String, quantity: Int32) -> [UInt8] {
        var body = [UInt8]()
        appendField(1, kind: 1, value: Array(sku.utf8), to: &body)
        appendField(
            2,
            kind: 2,
            value: withUnsafeBytes(of: quantity.littleEndian, Array.init),
            to: &body)
        var result = Array("NLF1".utf8)
        appendU16(1, to: &result)
        appendU16(2, to: &result)
        appendU32(UInt32(body.count), to: &result)
        result.append(contentsOf: body)
        return result
    }

#if NETLIFT_V5_ASYNC_RESULTS && NETLIFT_V5_HOST_CODECS
    private func asyncResultPayload(
        code: Int32,
        accepted: Bool,
        audited: Bool? = nil
    ) -> [UInt8] {
        var body = [UInt8]()
        appendField(
            1,
            kind: 2,
            value: withUnsafeBytes(of: code.littleEndian, Array.init),
            to: &body)
        appendField(2, kind: 3, value: [accepted ? 1 : 0], to: &body)
        if let audited {
            appendField(9, kind: 3, value: [audited ? 1 : 0], to: &body)
        }
        var result = Array("NLF1".utf8)
        appendU16(1, to: &result)
        appendU16(audited == nil ? 2 : 3, to: &result)
        appendU32(UInt32(body.count), to: &result)
        result.append(contentsOf: body)
        return result
    }
#endif

    private func fieldIDs(in payload: [UInt8]) throws -> [UInt16] {
        guard payload.count >= 12,
              Array(payload.prefix(4)) == Array("NLF1".utf8) else {
            throw PayloadError.malformed
        }
        let fieldCount = Int(payload[6]) | (Int(payload[7]) << 8)
        var offset = 12
        var result = [UInt16]()
        for _ in 0..<fieldCount {
            guard offset <= payload.count - 7 else {
                throw PayloadError.malformed
            }
            let fieldID = UInt16(payload[offset]) | (UInt16(payload[offset + 1]) << 8)
            let length = Int(payload[offset + 3])
                | (Int(payload[offset + 4]) << 8)
                | (Int(payload[offset + 5]) << 16)
                | (Int(payload[offset + 6]) << 24)
            guard length >= 0, length <= payload.count - offset - 7 else {
                throw PayloadError.malformed
            }
            result.append(fieldID)
            offset += 7 + length
        }
        guard offset == payload.count else {
            throw PayloadError.malformed
        }
        return result
    }

    private func appendField(_ id: UInt16, kind: UInt8, value: [UInt8], to bytes: inout [UInt8]) {
        appendU16(id, to: &bytes)
        bytes.append(kind)
        appendU32(UInt32(value.count), to: &bytes)
        bytes.append(contentsOf: value)
    }

    private func appendU16(_ value: UInt16, to bytes: inout [UInt8]) {
        bytes.append(UInt8(truncatingIfNeeded: value))
        bytes.append(UInt8(truncatingIfNeeded: value >> 8))
    }

    private func appendU32(_ value: UInt32, to bytes: inout [UInt8]) {
        bytes.append(UInt8(truncatingIfNeeded: value))
        bytes.append(UInt8(truncatingIfNeeded: value >> 8))
        bytes.append(UInt8(truncatingIfNeeded: value >> 16))
        bytes.append(UInt8(truncatingIfNeeded: value >> 24))
    }

    private func managedResultExpectation(endpointToken: UInt64) -> ManagedResultSchemaExpectation {
        ManagedResultSchemaExpectation(
            endpointToken: endpointToken,
            abiMajor: 1,
            schemaVersion: 5,
            contractMajor: 1,
            contractMinor: 0,
            featureBits: 0x36,
            requiredFeatureBits: 0x36,
            profileID: 1,
            serviceID: 17,
            methodID: 1,
            resultDTOID: 22,
            resultDTOVersion: 1,
            localSchemaHash: asyncOldSchemaHash,
            peerSchemaHash: asyncNewSchemaHash,
            fields: [
                .init(id: 1, kind: .i32, nullable: false, optional: false),
                .init(id: 2, kind: .bool, nullable: false, optional: false),
            ])
    }

    private func managedResultDescriptor(endpointToken: UInt64) -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: 144)
        bytes.replaceSubrange(0..<4, with: "NLMR".utf8)
        setU16(1, at: 4, in: &bytes)
        setU16(136, at: 6, in: &bytes)
        setU32(144, at: 8, in: &bytes)
        setU32(1, at: 12, in: &bytes)
        setU64(endpointToken, at: 16, in: &bytes)
        setU32(1, at: 24, in: &bytes)
        setU32(1, at: 28, in: &bytes)
        setU32(5, at: 32, in: &bytes)
        setU32(1, at: 36, in: &bytes)
        setU32(0, at: 40, in: &bytes)
        setU32(0x36, at: 44, in: &bytes)
        setU32(0x36, at: 48, in: &bytes)
        setU32(17, at: 52, in: &bytes)
        setU32(1, at: 56, in: &bytes)
        setU32(22, at: 60, in: &bytes)
        setU32(1, at: 64, in: &bytes)
        setU16(2, at: 68, in: &bytes)
        setU16(4, at: 70, in: &bytes)
        bytes.replaceSubrange(72..<104, with: hexBytes(asyncOldSchemaHash))
        bytes.replaceSubrange(104..<136, with: hexBytes(asyncNewSchemaHash))
        setU16(1, at: 136, in: &bytes)
        bytes[138] = NetLiftV5PeerFieldKind.i32.rawValue
        setU16(2, at: 140, in: &bytes)
        bytes[142] = NetLiftV5PeerFieldKind.bool.rawValue
        return bytes
    }

    private func setU16(_ value: UInt16, at offset: Int, in bytes: inout [UInt8]) {
        bytes[offset] = UInt8(truncatingIfNeeded: value)
        bytes[offset + 1] = UInt8(truncatingIfNeeded: value >> 8)
    }

    private func setU32(_ value: UInt32, at offset: Int, in bytes: inout [UInt8]) {
        bytes[offset] = UInt8(truncatingIfNeeded: value)
        bytes[offset + 1] = UInt8(truncatingIfNeeded: value >> 8)
        bytes[offset + 2] = UInt8(truncatingIfNeeded: value >> 16)
        bytes[offset + 3] = UInt8(truncatingIfNeeded: value >> 24)
    }

    private func setU64(_ value: UInt64, at offset: Int, in bytes: inout [UInt8]) {
        setU32(UInt32(truncatingIfNeeded: value), at: offset, in: &bytes)
        setU32(UInt32(truncatingIfNeeded: value >> 32), at: offset + 4, in: &bytes)
    }

    private func hexBytes(_ value: String) -> [UInt8] {
        stride(from: 0, to: value.count, by: 2).map { offset in
            let start = value.index(value.startIndex, offsetBy: offset)
            let end = value.index(start, offsetBy: 2)
            return UInt8(value[start..<end], radix: 16)!
        }
    }
}
