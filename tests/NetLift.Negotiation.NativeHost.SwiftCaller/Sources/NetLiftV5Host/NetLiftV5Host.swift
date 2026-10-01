import Foundation
import NetLiftV5NativeBridge

public enum NetLiftV5PeerFieldKind: UInt8, Sendable, Equatable {
    case utf8 = 1
    case i32 = 2
    case bool = 3
}

public struct NetLiftV5PeerField: Sendable, Equatable {
    public let id: UInt16
    public let kind: NetLiftV5PeerFieldKind
    public let nullable: Bool
    public let optional: Bool

    internal init(id: UInt16, kind: NetLiftV5PeerFieldKind, flags: UInt8) {
        self.id = id
        self.kind = kind
        nullable = flags & 0x1 != 0
        optional = flags & 0x2 != 0
    }
}

internal final class NetLiftV5EndpointLifetime: @unchecked Sendable {
    private let lock = NSLock()
    private var active = true

    var isActive: Bool {
        lock.lock()
        defer { lock.unlock() }
        return active
    }

    func invalidate() {
        lock.lock()
        active = false
        lock.unlock()
    }
}

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

    fileprivate let endpointIdentity: UUID
    private let lifetime: NetLiftV5EndpointLifetime

    fileprivate init(
        abiMajor: UInt32,
        schemaVersion: UInt32,
        contractMajor: UInt32,
        contractMinor: UInt32,
        featureBits: UInt32,
        requiredFeatureBits: UInt32,
        serviceID: UInt32,
        methodID: UInt32,
        dtoID: UInt32,
        dtoVersion: UInt32,
        localSchemaHash: String,
        schemaHash: String,
        fields: [NetLiftV5PeerField],
        endpointIdentity: UUID,
        lifetime: NetLiftV5EndpointLifetime
    ) {
        self.abiMajor = abiMajor
        self.schemaVersion = schemaVersion
        self.contractMajor = contractMajor
        self.contractMinor = contractMinor
        self.featureBits = featureBits
        self.requiredFeatureBits = requiredFeatureBits
        self.serviceID = serviceID
        self.methodID = methodID
        self.dtoID = dtoID
        self.dtoVersion = dtoVersion
        self.localSchemaHash = localSchemaHash
        self.schemaHash = schemaHash
        self.fields = fields
        self.endpointIdentity = endpointIdentity
        self.lifetime = lifetime
    }

    public func requireActive() throws {
        guard lifetime.isActive else {
            throw NetLiftV5HostError.endpointClosed
        }
    }

    public static func == (left: NetLiftV5PeerContext, right: NetLiftV5PeerContext) -> Bool {
        left.abiMajor == right.abiMajor &&
            left.schemaVersion == right.schemaVersion &&
            left.contractMajor == right.contractMajor &&
            left.contractMinor == right.contractMinor &&
            left.featureBits == right.featureBits &&
            left.requiredFeatureBits == right.requiredFeatureBits &&
            left.serviceID == right.serviceID &&
            left.methodID == right.methodID &&
            left.dtoID == right.dtoID &&
            left.dtoVersion == right.dtoVersion &&
            left.localSchemaHash == right.localSchemaHash &&
            left.schemaHash == right.schemaHash &&
            left.fields == right.fields &&
            left.endpointIdentity == right.endpointIdentity
    }
}

public enum NetLiftV5HostError: Error, Sendable, Equatable, LocalizedError {
    case native(status: UInt32, message: String)
    case invalidPeerSnapshot(String)
    case invalidManagedResultSchemaSnapshot(String)
    case localManifestMismatch(expected: String, actual: String)
    case managedResultSchemaUnavailable
    case endpointClosed
    case endpointNotStarted
    case contextBelongsToAnotherEndpoint

    public var errorDescription: String? {
        switch self {
        case let .native(status, message):
            return "Native v5 host returned status \(status): \(message)"
        case let .invalidPeerSnapshot(message):
            return "Invalid NLPS v1 peer snapshot: \(message)"
        case let .invalidManagedResultSchemaSnapshot(message):
            return "Invalid NLMR v1 managed-result schema snapshot: \(message)"
        case let .localManifestMismatch(expected, actual):
            return "Generated codec hash \(expected) does not match managed local manifest hash \(actual)."
        case .managedResultSchemaUnavailable:
            return "The managed endpoint does not use the AsyncCallbackV1 result profile."
        case .endpointClosed:
            return "The compatible-start endpoint is closed."
        case .endpointNotStarted:
            return "The compatible-start endpoint has not successfully started."
        case .contextBelongsToAnotherEndpoint:
            return "The peer context belongs to a different endpoint."
        }
    }
}

public enum NetLiftV5Flow: Sendable {
    case oldLocalWithNewPeer
    case newLocalWithOldPeer
}

public struct NetLiftV5Counters: Sendable, Equatable {
    public let factoryCalls: UInt32
    public let routeCalls: UInt32
    public let lastQuantity: Int32
    public let optionalDefaultsValid: UInt32
}

public final class NetLiftV5Host {
    fileprivate let nativeHost: OpaquePointer

    private init(nativeHost: OpaquePointer) {
        self.nativeHost = nativeHost
    }

    public static func load(
        dotnetRoot: URL,
        oldAssembly: URL,
        oldRuntimeConfig: URL,
        newAssembly: URL,
        newRuntimeConfig: URL
    ) throws -> NetLiftV5Host {
        var nativeHost: OpaquePointer?
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = dotnetRoot.path.withCString { dotnetRootPath in
            oldAssembly.path.withCString { oldAssemblyPath in
                oldRuntimeConfig.path.withCString { oldRuntimeConfigPath in
                    newAssembly.path.withCString { newAssemblyPath in
                        newRuntimeConfig.path.withCString { newRuntimeConfigPath in
                            errorBuffer.withUnsafeMutableBufferPointer { error in
                                nlv5_host_create(
                                    dotnetRootPath,
                                    oldAssemblyPath,
                                    oldRuntimeConfigPath,
                                    newAssemblyPath,
                                    newRuntimeConfigPath,
                                    &nativeHost,
                                    error.baseAddress,
                                    error.count)
                            }
                        }
                    }
                }
            }
        }
        guard status == 0, let nativeHost else {
            throw Self.nativeError(status: status, buffer: errorBuffer)
        }
        return NetLiftV5Host(nativeHost: nativeHost)
    }

    public func makeEndpoint(for flow: NetLiftV5Flow) throws -> NetLiftV5Endpoint {
        let nativeFlow: nlv5_flow
        switch flow {
        case .oldLocalWithNewPeer:
            nativeFlow = NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER
        case .newLocalWithOldPeer:
            nativeFlow = NLV5_FLOW_NEW_LOCAL_WITH_OLD_PEER
        }

        var nativeEndpoint: OpaquePointer?
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = errorBuffer.withUnsafeMutableBufferPointer { error in
            nlv5_endpoint_create(
                nativeHost,
                nativeFlow,
                &nativeEndpoint,
                error.baseAddress,
                error.count)
        }
        guard status == 0, let nativeEndpoint else {
            throw Self.nativeError(status: status, buffer: errorBuffer)
        }
        return NetLiftV5Endpoint(
            host: self,
            nativeEndpoint: nativeEndpoint,
            flow: flow)
    }

    public func start(
        for flow: NetLiftV5Flow,
        expectedManagedSchemaHash: String
    ) throws -> NetLiftV5Endpoint {
        let endpoint = try makeEndpoint(for: flow)
        try endpoint.start(expectedManagedSchemaHash: expectedManagedSchemaHash)
        return endpoint
    }

    fileprivate func validateLocalManifest(
        flow: NetLiftV5Flow,
        expectedManagedSchemaHash: String
    ) throws {
        let nativeFlow: nlv5_flow
        switch flow {
        case .oldLocalWithNewPeer:
            nativeFlow = NLV5_FLOW_OLD_LOCAL_WITH_NEW_PEER
        case .newLocalWithOldPeer:
            nativeFlow = NLV5_FLOW_NEW_LOCAL_WITH_OLD_PEER
        }
        var bytes: UnsafeMutablePointer<UInt8>?
        var length: UInt32 = 0
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = errorBuffer.withUnsafeMutableBufferPointer { error in
            nlv5_host_get_manifest(
                nativeHost,
                nativeFlow,
                &bytes,
                &length,
                error.baseAddress,
                error.count)
        }
        guard status == 0, let bytes else {
            throw Self.nativeError(status: status, buffer: errorBuffer)
        }
        defer { nlv5_free_peer_schema(bytes) }
        let data = Data(bytes: bytes, count: Int(length))
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let actualSchemaHash = root["schemaHash"] as? String else {
            throw NetLiftV5HostError.invalidPeerSnapshot(
                "managed local manifest has no schemaHash")
        }
        guard actualSchemaHash == expectedManagedSchemaHash else {
            throw NetLiftV5HostError.localManifestMismatch(
                expected: expectedManagedSchemaHash,
                actual: actualSchemaHash)
        }
    }

    deinit {
        nlv5_host_destroy(nativeHost)
    }

    fileprivate static func nativeError(
        status: UInt32,
        buffer: [CChar]
    ) -> NetLiftV5HostError {
        let message = buffer.withUnsafeBufferPointer { pointer in
            guard let base = pointer.baseAddress else { return "" }
            return String(cString: base)
        }
        return .native(status: status, message: message)
    }
}

public final class NetLiftV5Endpoint {
    private let host: NetLiftV5Host
    private let nativeEndpoint: OpaquePointer
    private let identity = UUID()
    private let lifetime = NetLiftV5EndpointLifetime()
    private let flow: NetLiftV5Flow
    private var started = false
    private var closed = false
    private var cachedPeerContext: NetLiftV5PeerContext?
    private var cachedManagedResultSchemaContext: ManagedResultSchemaContext?
    private var localSchemaHash: String?

    fileprivate init(
        host: NetLiftV5Host,
        nativeEndpoint: OpaquePointer,
        flow: NetLiftV5Flow
    ) {
        self.host = host
        self.nativeEndpoint = nativeEndpoint
        self.flow = flow
    }

    public func start(expectedManagedSchemaHash: String) throws {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        try host.validateLocalManifest(flow: flow, expectedManagedSchemaHash: expectedManagedSchemaHash)
        try start(mode: NLV5_START_PINNED)
        localSchemaHash = expectedManagedSchemaHash
    }

    func startWithWrongPinForTesting(expectedManagedSchemaHash: String) throws {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        try host.validateLocalManifest(flow: flow, expectedManagedSchemaHash: expectedManagedSchemaHash)
        try start(mode: NLV5_START_WRONG_PIN_TEST)
    }

    private func start(mode: nlv5_start_mode) throws {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = errorBuffer.withUnsafeMutableBufferPointer { error in
            nlv5_endpoint_start(nativeEndpoint, mode, error.baseAddress, error.count)
        }
        guard status == 0 else {
            throw NetLiftV5Host.nativeError(status: status, buffer: errorBuffer)
        }
        started = true
    }

    public func peerContext() throws -> NetLiftV5PeerContext {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        if let cachedPeerContext {
            return cachedPeerContext
        }

        var bytes: UnsafeMutablePointer<UInt8>?
        var length: UInt32 = 0
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = errorBuffer.withUnsafeMutableBufferPointer { error in
            nlv5_endpoint_get_peer_schema(
                nativeEndpoint,
                &bytes,
                &length,
                error.baseAddress,
                error.count)
        }
        guard status == 0, let bytes else {
            throw NetLiftV5Host.nativeError(status: status, buffer: errorBuffer)
        }
        defer { nlv5_free_peer_schema(bytes) }
        guard started, let localSchemaHash else {
            throw NetLiftV5HostError.endpointNotStarted
        }

        let data = Data(bytes: bytes, count: Int(length))
        let peerContext = try Self.validatePeerSnapshot(
            data,
            expected: expectedPeer(for: flow),
            localSchemaHash: localSchemaHash,
            endpointIdentity: identity,
            lifetime: lifetime)
        cachedPeerContext = peerContext
        return peerContext
    }

    public func managedResultSchemaContext() throws -> ManagedResultSchemaContext {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard started else { throw NetLiftV5HostError.endpointNotStarted }
        if let cachedManagedResultSchemaContext {
            return cachedManagedResultSchemaContext
        }
        guard let localSchemaHash else {
            throw NetLiftV5HostError.endpointNotStarted
        }

        #if NETLIFT_V5_ASYNC_RESULTS
        let peer = try peerContext()
        var bytesWritten: UInt32 = 0
        var requiredBytes: UInt32 = 0
        let probeStatus = nlv5_endpoint_get_managed_result_schema(
            nativeEndpoint,
            peer.serviceID,
            peer.methodID,
            nil,
            0,
            &bytesWritten,
            &requiredBytes)
        guard probeStatus == 12,
              bytesWritten == 0,
              (136...(136 + 4 * 64)).contains(Int(requiredBytes)) else {
            throw NetLiftV5HostError.native(
                status: probeStatus,
                message: "Managed result schema size probe failed.")
        }

        var bytes = [UInt8](repeating: 0, count: Int(requiredBytes))
        let status = bytes.withUnsafeMutableBufferPointer { buffer in
            nlv5_endpoint_get_managed_result_schema(
                nativeEndpoint,
                peer.serviceID,
                peer.methodID,
                buffer.baseAddress,
                UInt32(buffer.count),
                &bytesWritten,
                &requiredBytes)
        }
        guard status == 0,
              bytesWritten == UInt32(bytes.count),
              requiredBytes == UInt32(bytes.count) else {
            throw NetLiftV5HostError.native(
                status: status,
                message: "Managed result schema retrieval failed.")
        }

        let expectation = expectedManagedResult(
            for: peer,
            localSchemaHash: localSchemaHash,
            endpointToken: nil)
        let context = try ManagedResultSchemaParser.parse(
            Data(bytes),
            expected: expectation,
            endpointIdentity: identity,
            lifetime: lifetime)
        cachedManagedResultSchemaContext = context
        return context
        #else
        throw NetLiftV5HostError.managedResultSchemaUnavailable
        #endif
    }

    public func dispatch(_ bytes: [UInt8], using context: NetLiftV5PeerContext) throws -> Int32 {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard started else { throw NetLiftV5HostError.endpointNotStarted }
        guard context.endpointIdentity == identity else {
            throw NetLiftV5HostError.contextBelongsToAnotherEndpoint
        }
        try context.requireActive()
        guard !bytes.isEmpty, bytes.count <= Int(UInt32.max) else {
            throw NetLiftV5HostError.native(
                status: 1,
                message: "Payload size is invalid.")
        }

        var routeValue: Int32 = 0
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = bytes.withUnsafeBufferPointer { payload in
            errorBuffer.withUnsafeMutableBufferPointer { error in
                nlv5_endpoint_dispatch(
                    nativeEndpoint,
                    payload.baseAddress,
                    UInt32(payload.count),
                    &routeValue,
                    error.baseAddress,
                    error.count)
            }
        }
        guard status == 0 else {
            throw NetLiftV5Host.nativeError(status: status, buffer: errorBuffer)
        }
        return routeValue
    }

    public func counters() throws -> NetLiftV5Counters {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        var factoryCalls: UInt32 = 0
        var routeCalls: UInt32 = 0
        var lastQuantity: Int32 = 0
        var defaultsValid: UInt32 = 0
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = errorBuffer.withUnsafeMutableBufferPointer { error in
            nlv5_endpoint_get_counters(
                nativeEndpoint,
                &factoryCalls,
                &routeCalls,
                &lastQuantity,
                &defaultsValid,
                error.baseAddress,
                error.count)
        }
        guard status == 0 else {
            throw NetLiftV5Host.nativeError(status: status, buffer: errorBuffer)
        }
        return NetLiftV5Counters(
            factoryCalls: factoryCalls,
            routeCalls: routeCalls,
            lastQuantity: lastQuantity,
            optionalDefaultsValid: defaultsValid)
    }

    public func close() throws {
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = errorBuffer.withUnsafeMutableBufferPointer { error in
            nlv5_endpoint_close(nativeEndpoint, error.baseAddress, error.count)
        }
        guard status == 0 else {
            throw NetLiftV5Host.nativeError(status: status, buffer: errorBuffer)
        }
        closed = true
        lifetime.invalidate()
        cachedPeerContext = nil
        cachedManagedResultSchemaContext = nil
    }

    deinit {
        lifetime.invalidate()
        nlv5_endpoint_destroy(nativeEndpoint)
    }

    private struct ExpectedPeer {
        let schemaHash: String
        let contractMinor: UInt32
        let featureBits: UInt32
        let requiredFeatureBits: UInt32
        let fields: [(id: UInt16, kind: NetLiftV5PeerFieldKind, flags: UInt8)]
    }

    private func expectedPeer(for flow: NetLiftV5Flow) -> ExpectedPeer {
        #if NETLIFT_V5_ASYNC_RESULTS
        switch flow {
        case .oldLocalWithNewPeer:
            return ExpectedPeer(
                schemaHash: "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4",
                contractMinor: 1,
                featureBits: 0x36,
                requiredFeatureBits: 0x36,
                fields: [
                    (1, .utf8, 0),
                    (2, .i32, 0),
                    (7, .utf8, 0x2),
                ])
        case .newLocalWithOldPeer:
            return ExpectedPeer(
                schemaHash: "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b",
                contractMinor: 0,
                featureBits: 0x36,
                requiredFeatureBits: 0x36,
                fields: [
                    (1, .utf8, 0),
                    (2, .i32, 0),
                ])
        }
        #else
        switch flow {
        case .oldLocalWithNewPeer:
            return ExpectedPeer(
                schemaHash: "9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245",
                contractMinor: 1,
                featureBits: 0x10,
                requiredFeatureBits: 0,
                fields: [
                    (1, .utf8, 0),
                    (2, .i32, 0),
                    (7, .utf8, 0x2),
                    (8, .utf8, 0x3),
                    (9, .bool, 0x2),
                ])
        case .newLocalWithOldPeer:
            return ExpectedPeer(
                schemaHash: "7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184",
                contractMinor: 0,
                featureBits: 0x10,
                requiredFeatureBits: 0,
                fields: [
                    (1, .utf8, 0),
                    (2, .i32, 0),
                ])
        }
        #endif
    }

    #if NETLIFT_V5_ASYNC_RESULTS
    private func expectedManagedResult(
        for peer: NetLiftV5PeerContext,
        localSchemaHash: String,
        endpointToken: UInt64?
    ) -> ManagedResultSchemaExpectation {
        let resultFields: [ManagedResultSchemaFieldExpectation]
        let contractMinor: UInt32
        switch flow {
        case .oldLocalWithNewPeer:
            contractMinor = 0
            resultFields = [
                .init(id: 1, kind: .i32, nullable: false, optional: false),
                .init(id: 2, kind: .bool, nullable: false, optional: false),
            ]
        case .newLocalWithOldPeer:
            contractMinor = 1
            resultFields = [
                .init(id: 1, kind: .i32, nullable: false, optional: false),
                .init(id: 2, kind: .bool, nullable: false, optional: false),
                .init(id: 9, kind: .bool, nullable: false, optional: true),
            ]
        }
        return ManagedResultSchemaExpectation(
            endpointToken: endpointToken,
            abiMajor: 1,
            schemaVersion: 5,
            contractMajor: 1,
            contractMinor: contractMinor,
            featureBits: 0x36,
            requiredFeatureBits: 0x36,
            profileID: 1,
            serviceID: peer.serviceID,
            methodID: peer.methodID,
            resultDTOID: 22,
            resultDTOVersion: 1,
            localSchemaHash: localSchemaHash,
            peerSchemaHash: peer.schemaHash,
            fields: resultFields)
    }
    #endif

    private static func validatePeerSnapshot(
        _ data: Data,
        expected: ExpectedPeer,
        localSchemaHash: String,
        endpointIdentity: UUID,
        lifetime: NetLiftV5EndpointLifetime
    ) throws -> NetLiftV5PeerContext {
        func fail(_ reason: String) -> NetLiftV5HostError {
            .invalidPeerSnapshot(reason)
        }
        func readU16(_ offset: Int) -> UInt16 {
            UInt16(data[offset]) | (UInt16(data[offset + 1]) << 8)
        }
        func readU32(_ offset: Int) -> UInt32 {
            UInt32(data[offset]) |
                (UInt32(data[offset + 1]) << 8) |
                (UInt32(data[offset + 2]) << 16) |
                (UInt32(data[offset + 3]) << 24)
        }

        guard data.count >= 88 else { throw fail("header is truncated") }
        guard data[0..<4].elementsEqual([0x4e, 0x4c, 0x50, 0x53]) else {
            throw fail("magic is not NLPS")
        }
        guard readU16(4) == 1, readU16(6) == 88 else {
            throw fail("descriptor version or header size is unsupported")
        }
        let totalLength = Int(readU32(8))
        let fieldCount = Int(readU16(52))
        let recordSize = Int(readU16(54))
        guard fieldCount <= 64, recordSize == 4,
              totalLength == 88 + fieldCount * recordSize,
              data.count == totalLength else {
            throw fail("total length or field-record bounds are invalid")
        }

        let abiMajor = readU32(12)
        let schemaVersion = readU32(16)
        let contractMajor = readU32(20)
        let contractMinor = readU32(24)
        let featureBits = readU32(28)
        let requiredFeatureBits = readU32(32)
        let serviceID = readU32(36)
        let methodID = readU32(40)
        let dtoID = readU32(44)
        let dtoVersion = readU32(48)
        let schemaHash = data[56..<88].map { String(format: "%02x", $0) }.joined()
        guard abiMajor == 1, schemaVersion == 5,
              contractMajor == 1, contractMinor == expected.contractMinor,
              featureBits == expected.featureBits,
              requiredFeatureBits == expected.requiredFeatureBits,
              serviceID == 17, methodID == 1,
              dtoID == 21, dtoVersion == 1,
              schemaHash == expected.schemaHash,
              fieldCount == expected.fields.count else {
            throw fail("snapshot identity does not match the pinned peer schema")
        }

        var fields = [NetLiftV5PeerField]()
        fields.reserveCapacity(fieldCount)
        var previousID: UInt16 = 0
        for index in 0..<fieldCount {
            let offset = 88 + index * 4
            let id = readU16(offset)
            let rawKind = data[offset + 2]
            let flags = data[offset + 3]
            guard id > previousID, flags & ~UInt8(0x3) == 0,
                  let kind = NetLiftV5PeerFieldKind(rawValue: rawKind),
                  flags & 0x1 == 0 || kind == .utf8 else {
                throw fail("field records are unsorted or contain unsupported kind/flags")
            }
            let expectedField = expected.fields[index]
            guard id == expectedField.id, kind == expectedField.kind,
                  flags == expectedField.flags else {
                throw fail("field record does not match the pinned generated peer")
            }
            fields.append(NetLiftV5PeerField(id: id, kind: kind, flags: flags))
            previousID = id
        }

        return NetLiftV5PeerContext(
            abiMajor: abiMajor,
            schemaVersion: schemaVersion,
            contractMajor: contractMajor,
            contractMinor: contractMinor,
            featureBits: featureBits,
            requiredFeatureBits: requiredFeatureBits,
            serviceID: serviceID,
            methodID: methodID,
            dtoID: dtoID,
            dtoVersion: dtoVersion,
            localSchemaHash: localSchemaHash,
            schemaHash: schemaHash,
            fields: fields,
            endpointIdentity: endpointIdentity,
            lifetime: lifetime)
    }
}
