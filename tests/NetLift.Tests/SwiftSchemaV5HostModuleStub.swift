import Foundation

public enum NetLiftV5PeerFieldKind {
    case utf8
    case i32
    case bool
}

public struct NetLiftV5PeerField {
    public let id: UInt16
    public let kind: NetLiftV5PeerFieldKind
    public let nullable: Bool
    public let optional: Bool
}

public enum NetLiftV5PeerContextTestMutation {
    case none
    case inactive
    case invalidLocalHash
    case wrongAbi
    case wrongSchemaVersion
    case wrongContractMajor
    case wrongMinor
    case unsupportedFeature
    case unsupportedRequiredFeature
    case wrongService
    case wrongMethod
    case wrongDto
    case wrongDtoVersion
    case wrongPeerHash
    case invalidPeerHash
    case unknownRequiredField
    case incompatibleKind
    case incompatibleNullability
    case incompatibleOptionality
    case duplicateField
}

public final class NetLiftV5PeerContext {
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
    private var active: Bool

    private init(
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
        active: Bool
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
        self.active = active
    }

    public func requireActive() throws {
        guard active else { throw TestPeerContextError.inactive }
    }

    public func revokeForTesting() {
        active = false
    }

    public static func testingOnly(
        contentsOf path: String,
        mutation: NetLiftV5PeerContextTestMutation = .none
    ) throws -> NetLiftV5PeerContext {
        let lines = try String(contentsOfFile: path, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
        var values = [String: String]()
        var fields = [NetLiftV5PeerField]()
        for line in lines {
            let pair = line.split(separator: "=", maxSplits: 1)
            guard pair.count == 2 else { throw TestPeerContextError.malformed }
            let key = String(pair[0])
            let value = String(pair[1])
            if key == "field" {
                let parts = value.split(separator: "|")
                guard parts.count == 4,
                      let id = UInt16(parts[0]),
                      parts[2] == "0" || parts[2] == "1",
                      parts[3] == "0" || parts[3] == "1" else {
                    throw TestPeerContextError.malformed
                }
                let nullable = parts[2] == "1"
                let optional = parts[3] == "1"
                let kind: NetLiftV5PeerFieldKind
                switch parts[1] {
                case "utf8": kind = .utf8
                case "i32": kind = .i32
                case "bool": kind = .bool
                default: throw TestPeerContextError.malformed
                }
                fields.append(NetLiftV5PeerField(
                    id: id, kind: kind, nullable: nullable, optional: optional))
            } else {
                values[key] = value
            }
        }
        func number(_ key: String) throws -> UInt32 {
            guard let text = values[key], let value = UInt32(text) else {
                throw TestPeerContextError.malformed
            }
            return value
        }
        func text(_ key: String) throws -> String {
            guard let value = values[key] else { throw TestPeerContextError.malformed }
            return value
        }
        var abiMajor = try number("abiMajor")
        var schemaVersion = try number("schemaVersion")
        var contractMajor = try number("contractMajor")
        var contractMinor = try number("contractMinor")
        var featureBits = try number("featureBits")
        var requiredFeatureBits = try number("requiredFeatureBits")
        var serviceID = try number("serviceID")
        var methodID = try number("methodID")
        var dtoID = try number("dtoID")
        var dtoVersion = try number("dtoVersion")
        var localSchemaHash = try text("localSchemaHash")
        var schemaHash = try text("schemaHash")
        var active = true
        switch mutation {
        case .none: break
        case .inactive: active = false
        case .invalidLocalHash: localSchemaHash = "not-a-hash"
        case .wrongAbi: abiMajor = 2
        case .wrongSchemaVersion: schemaVersion = 4
        case .wrongContractMajor: contractMajor &+= 1
        case .wrongMinor: contractMinor = contractMinor == 0 ? 1 : 0
        case .unsupportedFeature: featureBits |= 0x20
        case .unsupportedRequiredFeature: requiredFeatureBits |= 0x1
        case .wrongService: serviceID &+= 1
        case .wrongMethod: methodID &+= 1
        case .wrongDto: dtoID &+= 1
        case .wrongDtoVersion: dtoVersion &+= 1
        case .wrongPeerHash: schemaHash = String(repeating: "0", count: 64)
        case .invalidPeerHash: schemaHash = "not-a-hash"
        case .unknownRequiredField:
            fields.append(NetLiftV5PeerField(id: 99, kind: .utf8, nullable: false, optional: false))
        case .incompatibleKind:
            guard let index = fields.firstIndex(where: { $0.id == 1 }) else {
                throw TestPeerContextError.malformed
            }
            fields[index] = NetLiftV5PeerField(
                id: 1, kind: .bool, nullable: false, optional: false)
        case .incompatibleNullability:
            guard let index = fields.firstIndex(where: { $0.id == 1 }) else {
                throw TestPeerContextError.malformed
            }
            fields[index] = NetLiftV5PeerField(
                id: 1, kind: .utf8, nullable: true, optional: false)
        case .incompatibleOptionality:
            guard let index = fields.firstIndex(where: { $0.id == 1 }) else {
                throw TestPeerContextError.malformed
            }
            fields[index] = NetLiftV5PeerField(
                id: 1, kind: .utf8, nullable: false, optional: true)
        case .duplicateField:
            guard let first = fields.first else { throw TestPeerContextError.malformed }
            fields.append(first)
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
            active: active)
    }
}

public enum TestPeerContextError: Error {
    case inactive
    case malformed
}
