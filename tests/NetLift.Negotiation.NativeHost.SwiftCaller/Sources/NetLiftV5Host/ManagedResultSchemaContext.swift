import Foundation

internal struct ManagedResultSchemaFieldExpectation: Sendable, Equatable {
    let id: UInt16
    let kind: NetLiftV5PeerFieldKind
    let nullable: Bool
    let optional: Bool
}

internal struct ManagedResultSchemaExpectation: Sendable {
    // Production token binding is performed in C so the opaque token never enters Swift.
    let endpointToken: UInt64?
    let abiMajor: UInt32
    let schemaVersion: UInt32
    let contractMajor: UInt32
    let contractMinor: UInt32
    let featureBits: UInt32
    let requiredFeatureBits: UInt32
    let profileID: UInt32
    let serviceID: UInt32
    let methodID: UInt32
    let resultDTOID: UInt32
    let resultDTOVersion: UInt32
    let localSchemaHash: String
    let peerSchemaHash: String
    let fields: [ManagedResultSchemaFieldExpectation]
}

public struct ManagedResultSchemaContext: Sendable, Equatable {
    public let abiMajor: UInt32
    public let schemaVersion: UInt32
    public let contractMajor: UInt32
    public let contractMinor: UInt32
    public let featureBits: UInt32
    public let requiredFeatureBits: UInt32
    public let profileId: UInt32
    public let serviceID: UInt32
    public let methodID: UInt32
    public let resultDTOID: UInt32
    public let resultDTOVersion: UInt32
    public let localSchemaHash: String
    public let peerSchemaHash: String
    public let fields: [NetLiftV5PeerField]

    internal let endpointIdentity: UUID
    private let lifetime: NetLiftV5EndpointLifetime

    internal init(
        abiMajor: UInt32,
        schemaVersion: UInt32,
        contractMajor: UInt32,
        contractMinor: UInt32,
        featureBits: UInt32,
        requiredFeatureBits: UInt32,
        profileId: UInt32,
        serviceID: UInt32,
        methodID: UInt32,
        resultDTOID: UInt32,
        resultDTOVersion: UInt32,
        localSchemaHash: String,
        peerSchemaHash: String,
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
        self.profileId = profileId
        self.serviceID = serviceID
        self.methodID = methodID
        self.resultDTOID = resultDTOID
        self.resultDTOVersion = resultDTOVersion
        self.localSchemaHash = localSchemaHash
        self.peerSchemaHash = peerSchemaHash
        self.fields = fields
        self.endpointIdentity = endpointIdentity
        self.lifetime = lifetime
    }

    public func requireActive() throws {
        guard lifetime.isActive else {
            throw NetLiftV5HostError.endpointClosed
        }
    }

    public static func == (
        left: ManagedResultSchemaContext,
        right: ManagedResultSchemaContext
    ) -> Bool {
        left.abiMajor == right.abiMajor &&
            left.schemaVersion == right.schemaVersion &&
            left.contractMajor == right.contractMajor &&
            left.contractMinor == right.contractMinor &&
            left.featureBits == right.featureBits &&
            left.requiredFeatureBits == right.requiredFeatureBits &&
            left.profileId == right.profileId &&
            left.serviceID == right.serviceID &&
            left.methodID == right.methodID &&
            left.resultDTOID == right.resultDTOID &&
            left.resultDTOVersion == right.resultDTOVersion &&
            left.localSchemaHash == right.localSchemaHash &&
            left.peerSchemaHash == right.peerSchemaHash &&
            left.fields == right.fields &&
            left.endpointIdentity == right.endpointIdentity
    }
}

internal enum ManagedResultSchemaParser {
    private static let headerSize = 136
    private static let maximumFieldCount = 64
    private static let fieldSize = 4

    static func parse(
        _ data: Data,
        expected: ManagedResultSchemaExpectation,
        endpointIdentity: UUID,
        lifetime: NetLiftV5EndpointLifetime
    ) throws -> ManagedResultSchemaContext {
        func fail(_ reason: String) -> NetLiftV5HostError {
            .invalidManagedResultSchemaSnapshot(reason)
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

        func readU64(_ offset: Int) -> UInt64 {
            UInt64(readU32(offset)) | (UInt64(readU32(offset + 4)) << 32)
        }

        func hashString(_ range: Range<Int>) -> String {
            data[range].map { String(format: "%02x", $0) }.joined()
        }

        guard data.count >= headerSize else {
            throw fail("header is truncated")
        }
        guard data[0..<4].elementsEqual([0x4e, 0x4c, 0x4d, 0x52]) else {
            throw fail("magic is not NLMR")
        }
        guard readU16(4) == 1, readU16(6) == headerSize else {
            throw fail("descriptor version or header size is unsupported")
        }

        let fieldCount = Int(readU16(68))
        let recordSize = Int(readU16(70))
        guard (1...maximumFieldCount).contains(fieldCount),
              recordSize == fieldSize,
              data.count <= headerSize + maximumFieldCount * fieldSize,
              Int(readU32(8)) == headerSize + fieldCount * fieldSize,
              data.count == headerSize + fieldCount * fieldSize else {
            throw fail("total length or field-record bounds are invalid")
        }

        let localSchemaHash = hashString(72..<104)
        let peerSchemaHash = hashString(104..<136)
        let endpointToken = readU64(16)
        guard endpointToken != 0,
              expected.endpointToken == nil || endpointToken == expected.endpointToken,
              readU32(12) == 1,
              readU32(24) == expected.profileID,
              readU32(28) == expected.abiMajor,
              readU32(32) == expected.schemaVersion,
              readU32(36) == expected.contractMajor,
              readU32(40) == expected.contractMinor,
              readU32(44) == expected.featureBits,
              readU32(48) == expected.requiredFeatureBits,
              readU32(52) == expected.serviceID,
              readU32(56) == expected.methodID,
              readU32(60) == expected.resultDTOID,
              readU32(64) == expected.resultDTOVersion,
              localSchemaHash == expected.localSchemaHash,
              peerSchemaHash == expected.peerSchemaHash else {
            throw fail("descriptor identity does not match the negotiated endpoint")
        }

        guard expected.fields.count == fieldCount else {
            throw fail("field count does not match the trusted result DTO")
        }

        var fields = [NetLiftV5PeerField]()
        fields.reserveCapacity(fieldCount)
        var previousID: UInt16 = 0
        for index in 0..<fieldCount {
            let offset = headerSize + index * fieldSize
            let id = readU16(offset)
            let rawKind = data[offset + 2]
            let flags = data[offset + 3]
            guard id > previousID,
                  flags & ~UInt8(0x3) == 0,
                  let kind = NetLiftV5PeerFieldKind(rawValue: rawKind),
                  flags & 0x1 == 0 || kind == .utf8 else {
                throw fail("field records are unsorted or contain unsupported kind/flags")
            }

            let expectedField = expected.fields[index]
            guard id == expectedField.id,
                  kind == expectedField.kind,
                  (flags & 0x1 != 0) == expectedField.nullable,
                  (flags & 0x2 != 0) == expectedField.optional else {
                throw fail("field record does not match the trusted result DTO")
            }
            fields.append(
                NetLiftV5PeerField(
                    id: id,
                    kind: kind,
                    flags: flags))
            previousID = id
        }

        return ManagedResultSchemaContext(
            abiMajor: readU32(28),
            schemaVersion: readU32(32),
            contractMajor: readU32(36),
            contractMinor: readU32(40),
            featureBits: readU32(44),
            requiredFeatureBits: readU32(48),
            profileId: readU32(24),
            serviceID: readU32(52),
            methodID: readU32(56),
            resultDTOID: readU32(60),
            resultDTOVersion: readU32(64),
            localSchemaHash: localSchemaHash,
            peerSchemaHash: peerSchemaHash,
            fields: fields,
            endpointIdentity: endpointIdentity,
            lifetime: lifetime)
    }
}
