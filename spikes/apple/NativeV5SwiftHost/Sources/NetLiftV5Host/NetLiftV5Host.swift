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
        self.nullable = flags & 0x1 != 0
        self.optional = flags & 0x2 != 0
    }
}

public struct NetLiftV5PeerContext: @unchecked Sendable, Equatable {
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
    fileprivate let lifetime: EndpointLifetime

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
        lifetime: EndpointLifetime
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
        guard lifetime.isActive else { throw NetLiftV5HostError.endpointClosed }
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.abiMajor == rhs.abiMajor &&
            lhs.schemaVersion == rhs.schemaVersion &&
            lhs.contractMajor == rhs.contractMajor &&
            lhs.contractMinor == rhs.contractMinor &&
            lhs.featureBits == rhs.featureBits &&
            lhs.requiredFeatureBits == rhs.requiredFeatureBits &&
            lhs.serviceID == rhs.serviceID &&
            lhs.methodID == rhs.methodID &&
            lhs.dtoID == rhs.dtoID &&
            lhs.dtoVersion == rhs.dtoVersion &&
            lhs.localSchemaHash == rhs.localSchemaHash &&
            lhs.schemaHash == rhs.schemaHash &&
            lhs.fields == rhs.fields &&
            lhs.endpointIdentity == rhs.endpointIdentity
    }
}

public struct NetLiftV5PeerDescriptor: Sendable {
    public let canonicalJson: String
    public let schemaHash: String
    fileprivate let contractMajor: UInt32
    fileprivate let contractMinor: UInt32
    fileprivate let featureBits: UInt32
    fileprivate let requiredFeatureBits: UInt32
    fileprivate let minimumPeerMajor: UInt32
    fileprivate let minimumPeerMinor: UInt32

    public init(canonicalJson: String, schemaHash: String) throws {
        guard let data = canonicalJson.data(using: .utf8),
              let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let embeddedHash = root["schemaHash"] as? String,
              embeddedHash == schemaHash,
              let schema = root["schema"] as? [String: Any],
              let revision = schema["contractRevision"] as? [String: Any],
              let major = revision["major"] as? NSNumber,
              let minor = revision["minor"] as? NSNumber,
              let features = schema["featureBits"] as? NSNumber,
              let requiredFeatures = schema["requiredFeatureBits"] as? NSNumber else {
            throw NetLiftV5HostError.invalidPeerDescriptor
        }
        self.canonicalJson = canonicalJson
        self.schemaHash = schemaHash
        self.contractMajor = major.uint32Value
        self.contractMinor = minor.uint32Value
        self.featureBits = features.uint32Value
        self.requiredFeatureBits = requiredFeatures.uint32Value
        self.minimumPeerMajor = major.uint32Value
        self.minimumPeerMinor = minor.uint32Value
    }

    fileprivate var peerHello: [UInt8] {
        var bytes = Array("NLCS".utf8)
        appendU16(1, to: &bytes)
        appendU32(1, to: &bytes)
        bytes.append(contentsOf: Self.decodeHash(schemaHash) ?? [])
        appendU32(contractMajor, to: &bytes)
        appendU32(contractMinor, to: &bytes)
        appendU32(featureBits, to: &bytes)
        return bytes
    }

    fileprivate var hashBytes: [UInt8]? { Self.decodeHash(schemaHash) }

    private static func decodeHash(_ value: String) -> [UInt8]? {
        guard value.utf8.count == 64 else { return nil }
        var bytes = [UInt8]()
        bytes.reserveCapacity(32)
        let input = Array(value.utf8)
        for index in stride(from: 0, to: input.count, by: 2) {
            guard let high = hex(input[index]), let low = hex(input[index + 1]) else { return nil }
            bytes.append((high << 4) | low)
        }
        return bytes
    }

    private static func hex(_ value: UInt8) -> UInt8? {
        switch value {
        case 48...57: return value - 48
        case 97...102: return value - 87
        case 65...70: return value - 55
        default: return nil
        }
    }

    private func appendU16(_ value: UInt16, to bytes: inout [UInt8]) {
        bytes.append(UInt8(truncatingIfNeeded: value))
        bytes.append(UInt8(truncatingIfNeeded: value >> 8))
    }

    private func appendU32(_ value: UInt32, to bytes: inout [UInt8]) {
        for shift in stride(from: 0, to: 32, by: 8) {
            bytes.append(UInt8(truncatingIfNeeded: value >> UInt32(shift)))
        }
    }
}

public enum NetLiftV5Flow: UInt32, Sendable, Equatable {
    case oldLocalWithNewPeer = 1
    case newLocalWithOldPeer = 2
}

#if NETLIFT_V5_ASYNC_EXECUTION
public enum NetLiftV5AsyncOperationMode: UInt32, Sendable {
    case typedReply = 0
    case cancelRequest = 1
    case cancelOperation = 2
}

public struct NetLiftV5AsyncOperationResult: Sendable {
    public let terminalStatus: UInt32
    public let requestCount: UInt32
    public let resultCount: UInt32
    public let operationToken: UInt64
    public let resultBytes: [UInt8]
}

public struct NetLiftV5AsyncDrainResult: Sendable {
    public let releaseDelta: UInt64
    public let outstandingLeases: UInt32
    public let resultDescriptorLeases: Int32
}
#endif

public struct NetLiftV5Counters: Sendable, Equatable {
    public let factoryCalls: UInt32
    public let routeCalls: UInt32
    public let lastQuantity: Int32
    public let optionalDefaultsValid: UInt32
}

public enum NetLiftV5HostError: Error, Sendable, Equatable, LocalizedError {
    case native(status: UInt32, message: String)
    case invalidPeerDescriptor
    case invalidPeerSnapshot(String)
    case invalidManagedResultSchemaSnapshot(String)
    case managedResultSchemaUnavailable
    case localManifestMismatch(expected: String, actual: String)
    case endpointClosed
    case endpointNotStarted
    case asyncRuntimeNotBound
    case asyncRuntimeStillBound
    case startAlreadyAttempted
    case contextBelongsToAnotherEndpoint

    public var errorDescription: String? {
        switch self {
        case let .native(status, message):
            return "Native/CoreCLR host returned \(status): \(message)"
        case .invalidPeerDescriptor:
            return "Generated peer descriptor is malformed or its hash does not match."
        case let .invalidPeerSnapshot(message):
            return "Invalid NLPS v1 peer snapshot: \(message)"
        case let .invalidManagedResultSchemaSnapshot(message):
            return "Invalid NLMR v1 managed-result schema snapshot: \(message)"
        case .managedResultSchemaUnavailable:
            return "The managed endpoint does not expose an AsyncCallbackV1 result schema."
        case let .localManifestMismatch(expected, actual):
            return "Expected managed schema \(expected), but loaded image reports \(actual)."
        case .endpointClosed:
            return "The compatible-start endpoint is closed."
        case .endpointNotStarted:
            return "The compatible-start endpoint has not completed."
        case .asyncRuntimeNotBound:
            return "The AsyncCallbackV1 V2 runtime is not bound to this endpoint."
        case .asyncRuntimeStillBound:
            return "The endpoint cannot close before V2 revoke, release, and unbind complete."
        case .startAlreadyAttempted:
            return "The compatible-start endpoint permits only one start attempt."
        case .contextBelongsToAnotherEndpoint:
            return "The peer context belongs to another endpoint."
        }
    }
}

public final class NetLiftV5Host: @unchecked Sendable {
    public init(bundleResources: URL = Bundle.main.resourceURL ?? Bundle.main.bundleURL) throws {
        var error = [CChar](repeating: 0, count: 1024)
        let result = bundleResources.path.withCString { path in
            error.withUnsafeMutableBufferPointer { buffer in
                nlv5_ios_host_start(path, buffer.baseAddress, buffer.count)
            }
        }
        try Self.check(result, error)
    }

    public func makeEndpoint(
        for flow: NetLiftV5Flow,
        peer: NetLiftV5PeerDescriptor
    ) throws -> NetLiftV5Endpoint {
        guard let hashBytes = peer.hashBytes, peer.peerHello.count == 54 else {
            throw NetLiftV5HostError.invalidPeerDescriptor
        }
        var token: UInt64 = 0
        var error = [CChar](repeating: 0, count: 1024)
        let result = error.withUnsafeMutableBufferPointer { buffer in
            nlv5_ios_create_endpoint(flow.rawValue, &token, buffer.baseAddress, buffer.count)
        }
        try Self.check(result, error)
        return NetLiftV5Endpoint(
            host: self,
            flow: flow,
            token: token,
            peer: peer,
            trustedHash: hashBytes)
    }

    fileprivate func managedManifest(for flow: NetLiftV5Flow) throws -> Data {
        var bytes: UnsafeMutablePointer<UInt8>?
        var length: UInt32 = 0
        var error = [CChar](repeating: 0, count: 1024)
        let result = error.withUnsafeMutableBufferPointer { buffer in
            nlv5_ios_get_manifest(
                flow.rawValue,
                &bytes,
                &length,
                buffer.baseAddress,
                buffer.count)
        }
        try Self.check(result, error)
        guard let bytes else { throw NetLiftV5HostError.invalidPeerSnapshot("managed manifest is empty") }
        defer { nlv5_ios_free(bytes) }
        return Data(bytes: bytes, count: Int(length))
    }

    fileprivate func start(
        flow: NetLiftV5Flow,
        token: UInt64,
        peer: NetLiftV5PeerDescriptor,
        trustedHash: [UInt8]
    ) throws {
        let manifest = Array(peer.canonicalJson.utf8)
        let hello = peer.peerHello
        var error = [CChar](repeating: 0, count: 1024)
        let result = manifest.withUnsafeBufferPointer { manifestBuffer in
            hello.withUnsafeBufferPointer { helloBuffer in
                trustedHash.withUnsafeBufferPointer { pinBuffer in
                    error.withUnsafeMutableBufferPointer { errorBuffer in
                        nlv5_ios_start_endpoint(
                            flow.rawValue,
                            token,
                            manifestBuffer.baseAddress,
                            UInt32(manifest.count),
                            helloBuffer.baseAddress,
                            UInt32(hello.count),
                            pinBuffer.baseAddress,
                            UInt32(trustedHash.count),
                            peer.minimumPeerMajor,
                            peer.minimumPeerMinor,
                            1,
                            errorBuffer.baseAddress,
                            errorBuffer.count)
                    }
                }
            }
        }
        try Self.check(result, error)
    }

    fileprivate func peerSnapshot(flow: NetLiftV5Flow, token: UInt64) throws -> Data {
        var bytes: UnsafeMutablePointer<UInt8>?
        var length: UInt32 = 0
        var error = [CChar](repeating: 0, count: 1024)
        let result = error.withUnsafeMutableBufferPointer { buffer in
            nlv5_ios_get_peer_schema(
                flow.rawValue,
                token,
                &bytes,
                &length,
                buffer.baseAddress,
                buffer.count)
        }
        try Self.check(result, error)
        guard let bytes else { throw NetLiftV5HostError.invalidPeerSnapshot("snapshot is empty") }
        defer { nlv5_ios_free(bytes) }
        return Data(bytes: bytes, count: Int(length))
    }

    fileprivate func managedResultSchemaSnapshot(
        flow: NetLiftV5Flow,
        token: UInt64,
        serviceID: UInt32,
        methodID: UInt32
    ) throws -> Data {
        var bytesWritten: UInt32 = 0
        var requiredBytes: UInt32 = 0
        let probeStatus = nlv5_ios_get_managed_result_schema(
            flow.rawValue,
            token,
            serviceID,
            methodID,
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

        var data = Data(count: Int(requiredBytes))
        let status = data.withUnsafeMutableBytes { buffer in
            nlv5_ios_get_managed_result_schema(
                flow.rawValue,
                token,
                serviceID,
                methodID,
                buffer.baseAddress?.assumingMemoryBound(to: UInt8.self),
                UInt32(buffer.count),
                &bytesWritten,
                &requiredBytes)
        }
        guard status == 0,
              bytesWritten == UInt32(data.count),
              requiredBytes == UInt32(data.count) else {
            throw NetLiftV5HostError.native(
                status: status,
                message: "Managed result schema retrieval failed.")
        }
        return data
    }

    fileprivate func dispatch(
        flow: NetLiftV5Flow,
        token: UInt64,
        payload: [UInt8]
    ) throws -> Int32 {
        var value: Int32 = 0
        var error = [CChar](repeating: 0, count: 1024)
        let result = payload.withUnsafeBufferPointer { payloadBuffer in
            error.withUnsafeMutableBufferPointer { errorBuffer in
                nlv5_ios_dispatch(
                    flow.rawValue,
                    token,
                    payloadBuffer.baseAddress,
                    UInt32(payload.count),
                    &value,
                    errorBuffer.baseAddress,
                    errorBuffer.count)
            }
        }
        try Self.check(result, error)
        return value
    }

    fileprivate func counters(for flow: NetLiftV5Flow) throws -> NetLiftV5Counters {
        var factory: UInt32 = 0
        var route: UInt32 = 0
        var quantity: Int32 = 0
        var defaults: UInt32 = 0
        var error = [CChar](repeating: 0, count: 1024)
        let result = error.withUnsafeMutableBufferPointer { buffer in
            nlv5_ios_get_counters(
                flow.rawValue,
                &factory,
                &route,
                &quantity,
                &defaults,
                buffer.baseAddress,
                buffer.count)
        }
        try Self.check(result, error)
        return NetLiftV5Counters(
            factoryCalls: factory,
            routeCalls: route,
            lastQuantity: quantity,
            optionalDefaultsValid: defaults)
    }

    fileprivate func close(flow: NetLiftV5Flow, token: UInt64) throws {
        var error = [CChar](repeating: 0, count: 1024)
        let result = error.withUnsafeMutableBufferPointer { buffer in
            nlv5_ios_close_endpoint(
                flow.rawValue,
                token,
                buffer.baseAddress,
                buffer.count)
        }
        try Self.check(result, error)
    }

    fileprivate static func check(_ result: UInt32, _ error: [CChar]) throws {
        guard result == 0 else {
            let message = error.withUnsafeBufferPointer { pointer in
                pointer.baseAddress.map(String.init(cString:)) ?? "Unknown native error."
            }
            throw NetLiftV5HostError.native(status: result, message: message)
        }
    }
}

public final class NetLiftV5Endpoint: @unchecked Sendable {
    private let host: NetLiftV5Host
    private let flow: NetLiftV5Flow
    private let token: UInt64
    private let peer: NetLiftV5PeerDescriptor
    private let trustedHash: [UInt8]
    private let identity = UUID()
    private let lifetime = EndpointLifetime()
    private let lock = NSLock()
    private var startAttempted = false
    private var started = false
    private var closed = false
    private var context: NetLiftV5PeerContext?
    private var peerSchemaBytes: Data?
    private var managedResultContext: ManagedResultSchemaContext?
    private var managedResultSchemaBytes: Data?
    private var asyncRuntimeBound = false
    private var localSchemaHash: String?

    fileprivate init(
        host: NetLiftV5Host,
        flow: NetLiftV5Flow,
        token: UInt64,
        peer: NetLiftV5PeerDescriptor,
        trustedHash: [UInt8]
    ) {
        self.host = host
        self.flow = flow
        self.token = token
        self.peer = peer
        self.trustedHash = trustedHash
    }

    public func start(expectedManagedSchemaHash: String) throws {
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard !startAttempted else { throw NetLiftV5HostError.startAlreadyAttempted }
        let manifestData = try host.managedManifest(for: flow)
        guard let root = try JSONSerialization.jsonObject(with: manifestData) as? [String: Any],
              let actualHash = root["schemaHash"] as? String else {
            throw NetLiftV5HostError.invalidPeerSnapshot("managed GetManifest omitted schemaHash")
        }
        guard actualHash == expectedManagedSchemaHash else {
            throw NetLiftV5HostError.localManifestMismatch(
                expected: expectedManagedSchemaHash,
                actual: actualHash)
        }
        startAttempted = true
        try host.start(flow: flow, token: token, peer: peer, trustedHash: trustedHash)
        localSchemaHash = actualHash
        started = true
    }

    public func peerContext() throws -> NetLiftV5PeerContext {
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard started, let localSchemaHash else { throw NetLiftV5HostError.endpointNotStarted }
        if let context { return context }
        let snapshot = try host.peerSnapshot(flow: flow, token: token)
        let parsed = try parsePeerSnapshot(
            snapshot,
            descriptor: peer,
            localSchemaHash: localSchemaHash,
            endpointIdentity: identity,
            lifetime: lifetime)
        peerSchemaBytes = snapshot
        context = parsed
        return parsed
    }

    public func managedResultSchemaContext() throws -> ManagedResultSchemaContext {
        let peerContext = try peerContext()
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard started, let localSchemaHash else { throw NetLiftV5HostError.endpointNotStarted }
        if let managedResultContext { return managedResultContext }

        let data = try host.managedResultSchemaSnapshot(
            flow: flow,
            token: token,
            serviceID: peerContext.serviceID,
            methodID: peerContext.methodID)
        let resultFields: [ManagedResultSchemaFieldExpectation]
        switch flow {
        case .oldLocalWithNewPeer:
            resultFields = [
                .init(id: 1, kind: .i32, nullable: false, optional: false),
                .init(id: 2, kind: .bool, nullable: false, optional: false),
            ]
        case .newLocalWithOldPeer:
            resultFields = [
                .init(id: 1, kind: .i32, nullable: false, optional: false),
                .init(id: 2, kind: .bool, nullable: false, optional: false),
                .init(id: 9, kind: .bool, nullable: false, optional: true),
            ]
        }
        let expected = ManagedResultSchemaExpectation(
            abiMajor: 1,
            schemaVersion: 5,
            contractMajor: 1,
            contractMinor: flow == .oldLocalWithNewPeer ? 0 : 1,
            featureBits: 0x36,
            requiredFeatureBits: 0x36,
            profileID: 1,
            serviceID: peerContext.serviceID,
            methodID: peerContext.methodID,
            resultDTOID: 22,
            resultDTOVersion: 1,
            localSchemaHash: localSchemaHash,
            peerSchemaHash: peerContext.schemaHash,
            fields: resultFields)
        let parsed = try ManagedResultSchemaParser.parse(
            data,
            expected: expected,
            endpointIdentity: identity,
            lifetime: lifetime)
        managedResultSchemaBytes = data
        managedResultContext = parsed
        return parsed
    }

    public func verifyWrongResultProfileAndRouteRejected() throws {
        let resultContext = try managedResultSchemaContext()
        let peer = try peerContext()
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard let data = managedResultSchemaBytes, let localSchemaHash else {
            throw NetLiftV5HostError.endpointNotStarted
        }
        let fields = resultContext.fields.map {
            ManagedResultSchemaFieldExpectation(
                id: $0.id,
                kind: $0.kind,
                nullable: $0.nullable,
                optional: $0.optional)
        }
        let expected = ManagedResultSchemaExpectation(
            abiMajor: resultContext.abiMajor,
            schemaVersion: resultContext.schemaVersion,
            contractMajor: resultContext.contractMajor,
            contractMinor: resultContext.contractMinor,
            featureBits: resultContext.featureBits,
            requiredFeatureBits: resultContext.requiredFeatureBits,
            profileID: resultContext.profileId,
            serviceID: peer.serviceID,
            methodID: peer.methodID,
            resultDTOID: resultContext.resultDTOID,
            resultDTOVersion: resultContext.resultDTOVersion,
            localSchemaHash: localSchemaHash,
            peerSchemaHash: peer.schemaHash,
            fields: fields)

        for (offset, value, label) in [(24, UInt32(2), "profile"), (56, UInt32(2), "route")] {
            var mutated = data
            mutated[offset] = UInt8(truncatingIfNeeded: value)
            mutated[offset + 1] = UInt8(truncatingIfNeeded: value >> 8)
            mutated[offset + 2] = UInt8(truncatingIfNeeded: value >> 16)
            mutated[offset + 3] = UInt8(truncatingIfNeeded: value >> 24)
            do {
                _ = try ManagedResultSchemaParser.parse(
                    mutated,
                    expected: expected,
                    endpointIdentity: identity,
                    lifetime: lifetime)
                throw NetLiftV5HostError.invalidManagedResultSchemaSnapshot(
                    "wrong \(label) was accepted")
            } catch NetLiftV5HostError.invalidManagedResultSchemaSnapshot(let reason)
                where reason != "wrong \(label) was accepted" {
                continue
            }
        }
    }

#if NETLIFT_V5_ASYNC_EXECUTION
    public var isAsyncRuntimeBound: Bool {
        lock.lock()
        defer { lock.unlock() }
        return asyncRuntimeBound
    }

    public func bindAsyncCallbackV2() throws -> (serviceID: UInt32, methodID: UInt32) {
        let peer = try peerContext()
        let resultContext = try managedResultSchemaContext()
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard started else { throw NetLiftV5HostError.endpointNotStarted }
        guard !asyncRuntimeBound,
              let peerBytes = peerSchemaBytes,
              let resultBytes = managedResultSchemaBytes else {
            throw NetLiftV5HostError.asyncRuntimeNotBound
        }

        var serviceID: UInt32 = 0
        var methodID: UInt32 = 0
        var error = [CChar](repeating: 0, count: 1024)
        let result = peerBytes.withUnsafeBytes { peerBuffer in
            resultBytes.withUnsafeBytes { resultBuffer in
                error.withUnsafeMutableBufferPointer { errorBuffer in
                    nlv5_ios_async_bind_endpoint(
                        flow.rawValue,
                        token,
                        peerBuffer.baseAddress?.assumingMemoryBound(to: UInt8.self),
                        UInt32(peerBuffer.count),
                        resultBuffer.baseAddress?.assumingMemoryBound(to: UInt8.self),
                        UInt32(resultBuffer.count),
                        &serviceID,
                        &methodID,
                        errorBuffer.baseAddress,
                        errorBuffer.count)
                }
            }
        }
        try NetLiftV5Host.check(result, error)
        asyncRuntimeBound = true
        guard serviceID == peer.serviceID,
              methodID == peer.methodID,
              serviceID == resultContext.serviceID,
              methodID == resultContext.methodID else {
            throw NetLiftV5HostError.invalidManagedResultSchemaSnapshot(
                "bound V2 route differs from the live NLPS/NLMR contexts")
        }
        return (serviceID, methodID)
    }

    public func runAsyncCallbackV2Operation(
        payload: [UInt8],
        mode: NetLiftV5AsyncOperationMode,
        accepted: Bool = false
    ) throws -> NetLiftV5AsyncOperationResult {
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard started else { throw NetLiftV5HostError.endpointNotStarted }
        guard asyncRuntimeBound else { throw NetLiftV5HostError.asyncRuntimeNotBound }
        guard !payload.isEmpty else { throw NetLiftV5HostError.invalidPeerDescriptor }

        var nativeResult = nlv5_ios_async_operation_result()
        var nativeBytes: UnsafeMutablePointer<UInt8>?
        var nativeLength: UInt32 = 0
        var error = [CChar](repeating: 0, count: 1024)
        let result = payload.withUnsafeBufferPointer { payloadBuffer in
            error.withUnsafeMutableBufferPointer { errorBuffer in
                nlv5_ios_async_run_operation(
                    flow.rawValue,
                    token,
                    payloadBuffer.baseAddress,
                    UInt32(payloadBuffer.count),
                    mode.rawValue,
                    accepted ? 1 : 0,
                    &nativeResult,
                    &nativeBytes,
                    &nativeLength,
                    errorBuffer.baseAddress,
                    errorBuffer.count)
            }
        }
        try NetLiftV5Host.check(result, error)
        let bytes: [UInt8]
        if let nativeBytes {
            defer { nlv5_ios_free(nativeBytes) }
            bytes = Array(UnsafeBufferPointer(start: nativeBytes, count: Int(nativeLength)))
        } else {
            bytes = []
        }
        return NetLiftV5AsyncOperationResult(
            terminalStatus: nativeResult.terminal_status,
            requestCount: nativeResult.request_count,
            resultCount: nativeResult.result_count,
            operationToken: nativeResult.operation_token,
            resultBytes: bytes)
    }

    public func drainAsyncCallbackV2() throws -> NetLiftV5AsyncDrainResult {
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard asyncRuntimeBound else { throw NetLiftV5HostError.asyncRuntimeNotBound }

        var releaseDelta: UInt64 = 0
        var outstandingLeases: UInt32 = UInt32.max
        var resultDescriptorLeases: Int32 = -1
        var error = [CChar](repeating: 0, count: 1024)
        let result = error.withUnsafeMutableBufferPointer { buffer in
            nlv5_ios_async_drain_endpoint(
                flow.rawValue,
                token,
                &releaseDelta,
                &outstandingLeases,
                &resultDescriptorLeases,
                buffer.baseAddress,
                buffer.count)
        }
        try NetLiftV5Host.check(result, error)
        asyncRuntimeBound = false
        return NetLiftV5AsyncDrainResult(
            releaseDelta: releaseDelta,
            outstandingLeases: outstandingLeases,
            resultDescriptorLeases: resultDescriptorLeases)
    }
#endif

    public func dispatch(_ payload: [UInt8], using context: NetLiftV5PeerContext) throws -> Int32 {
        try context.requireActive()
        guard context.endpointIdentity == identity else {
            throw NetLiftV5HostError.contextBelongsToAnotherEndpoint
        }
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { throw NetLiftV5HostError.endpointClosed }
        guard started else { throw NetLiftV5HostError.endpointNotStarted }
        return try host.dispatch(flow: flow, token: token, payload: payload)
    }

    public func counters() throws -> NetLiftV5Counters {
        try host.counters(for: flow)
    }

    public func close() throws {
        lock.lock()
        defer { lock.unlock() }
        guard !closed else { return }
        guard !asyncRuntimeBound else { throw NetLiftV5HostError.asyncRuntimeStillBound }
        closed = true
        lifetime.invalidate()
        context = nil
        peerSchemaBytes = nil
        managedResultContext = nil
        managedResultSchemaBytes = nil
        try host.close(flow: flow, token: token)
    }

    deinit {
        lifetime.invalidate()
        if !closed && !asyncRuntimeBound {
            try? host.close(flow: flow, token: token)
        }
    }
}

internal final class EndpointLifetime: @unchecked Sendable {
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

private func parsePeerSnapshot(
    _ data: Data,
    descriptor: NetLiftV5PeerDescriptor,
    localSchemaHash: String,
    endpointIdentity: UUID,
    lifetime: EndpointLifetime
) throws -> NetLiftV5PeerContext {
    func fail(_ message: String) -> NetLiftV5HostError { .invalidPeerSnapshot(message) }
    func u16(_ offset: Int) -> UInt16 {
        UInt16(data[offset]) | UInt16(data[offset + 1]) << 8
    }
    func u32(_ offset: Int) -> UInt32 {
        UInt32(data[offset]) |
            UInt32(data[offset + 1]) << 8 |
            UInt32(data[offset + 2]) << 16 |
            UInt32(data[offset + 3]) << 24
    }
    guard data.count >= 88, data.prefix(4).elementsEqual("NLPS".utf8) else {
        throw fail("header or NLPS magic is invalid")
    }
    guard u16(4) == 1, u16(6) == 88 else { throw fail("descriptor version/header size is invalid") }
    let count = Int(u16(52))
    guard count <= 64, u16(54) == 4,
          Int(u32(8)) == 88 + count * 4,
          data.count == 88 + count * 4 else {
        throw fail("field count or length is invalid")
    }
    let hash = data[56..<88].map { String(format: "%02x", $0) }.joined()
    let schemaData = try JSONSerialization.jsonObject(with: Data(descriptor.canonicalJson.utf8))
    guard let root = schemaData as? [String: Any],
          let schema = root["schema"] as? [String: Any],
          let service = schema["service"] as? [String: Any],
          let method = schema["method"] as? [String: Any] else {
        throw fail("generated peer descriptor is malformed")
    }
    let dto: [String: Any]?
    if let legacyDTO = schema["dto"] as? [String: Any] {
        dto = legacyDTO
    } else if let requestDTOID = method["requestDtoId"] as? NSNumber,
              let dtos = schema["dtos"] as? [[String: Any]] {
        dto = dtos.first {
            ($0["id"] as? NSNumber)?.uint32Value == requestDTOID.uint32Value
        }
    } else {
        dto = nil
    }
    guard let dto, let descriptorFields = dto["fields"] as? [[String: Any]] else {
        throw fail("generated peer descriptor does not identify its request DTO")
    }
    let expectedFields = descriptorFields.compactMap { field -> (UInt16, NetLiftV5PeerFieldKind, UInt8)? in
        guard let number = field["id"] as? NSNumber,
              let kindString = field["type"] as? String,
              let kind = ["utf8": NetLiftV5PeerFieldKind.utf8,
                          "i32": .i32,
                          "bool": .bool][kindString] else { return nil }
        let nullable = (field["nullable"] as? NSNumber)?.boolValue ?? false
        let optional = (field["optional"] as? NSNumber)?.boolValue ?? false
        return (number.uint16Value, kind, (nullable ? 1 : 0) | (optional ? 2 : 0))
    }
    guard expectedFields.count == descriptorFields.count,
          hash == descriptor.schemaHash,
          u32(12) == 1, u32(16) == 5,
          u32(20) == descriptor.contractMajor,
          u32(24) == descriptor.contractMinor,
          u32(28) == descriptor.featureBits,
          u32(32) == descriptor.requiredFeatureBits,
          u32(36) == ((service["id"] as? NSNumber)?.uint32Value ?? 0),
          u32(40) == ((method["id"] as? NSNumber)?.uint32Value ?? 0),
          u32(44) == ((dto["id"] as? NSNumber)?.uint32Value ?? 0),
          u32(48) == ((dto["version"] as? NSNumber)?.uint32Value ?? 0),
          count == expectedFields.count else {
        throw fail("negotiated peer identity differs from generated peer codec")
    }
    var fields = [NetLiftV5PeerField]()
    var previous: UInt16 = 0
    for index in 0..<count {
        let offset = 88 + index * 4
        let id = u16(offset)
        let kind = NetLiftV5PeerFieldKind(rawValue: data[offset + 2])
        let flags = data[offset + 3]
        guard let kind, id > previous, flags & ~UInt8(3) == 0,
              expectedFields[index].0 == id,
              expectedFields[index].1 == kind,
              expectedFields[index].2 == flags else {
            throw fail("negotiated field differs from generated peer codec")
        }
        fields.append(NetLiftV5PeerField(id: id, kind: kind, flags: flags))
        previous = id
    }
    return NetLiftV5PeerContext(
        abiMajor: u32(12),
        schemaVersion: u32(16),
        contractMajor: u32(20),
        contractMinor: u32(24),
        featureBits: u32(28),
        requiredFeatureBits: u32(32),
        serviceID: u32(36),
        methodID: u32(40),
        dtoID: u32(44),
        dtoVersion: u32(48),
        localSchemaHash: localSchemaHash,
        schemaHash: hash,
        fields: fields,
        endpointIdentity: endpointIdentity,
        lifetime: lifetime)
}
