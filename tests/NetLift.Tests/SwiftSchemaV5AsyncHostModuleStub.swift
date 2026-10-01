import Foundation

public enum NetLiftV5PeerFieldKind: Equatable {
    case utf8
    case i32
    case bool
}

public struct NetLiftV5PeerField {
    public let id: UInt16
    public let kind: NetLiftV5PeerFieldKind
    public let nullable: Bool
    public let optional: Bool

    public init(id: UInt16, kind: NetLiftV5PeerFieldKind, nullable: Bool, optional: Bool) {
        self.id = id
        self.kind = kind
        self.nullable = nullable
        self.optional = optional
    }
}

public enum AsyncHostContextTestMutation: CaseIterable {
    case none
    case inactive
    case invalidLocalHash
    case wrongPeerHash
    case wrongProfile
    case wrongAbi
    case wrongFeatures
    case wrongContract
    case wrongService
    case wrongMethod
    case wrongDto
    case wrongDtoVersion
    case unknownRequiredField
    case incompatibleKind
    case duplicateField
    case missingRequiredField
}

public enum AsyncHostContextError: Error {
    case inactive
    case malformed
}

private struct AsyncHostContextFixture {
    var values: [String: String]
    var fields: [NetLiftV5PeerField]
    var active = true

    init(path: String, mutation: AsyncHostContextTestMutation) throws {
        let lines = try String(contentsOfFile: path, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
        var values = [String: String]()
        var fields = [NetLiftV5PeerField]()
        for line in lines {
            let pair = line.split(separator: "=", maxSplits: 1)
            guard pair.count == 2 else { throw AsyncHostContextError.malformed }
            let key = String(pair[0])
            let value = String(pair[1])
            if key == "field" {
                let parts = value.split(separator: "|", omittingEmptySubsequences: false)
                guard parts.count == 4,
                      let id = UInt16(parts[0]),
                      parts[2] == "0" || parts[2] == "1",
                      parts[3] == "0" || parts[3] == "1" else {
                    throw AsyncHostContextError.malformed
                }
                let kind: NetLiftV5PeerFieldKind
                switch parts[1] {
                case "utf8": kind = .utf8
                case "i32": kind = .i32
                case "bool": kind = .bool
                default: throw AsyncHostContextError.malformed
                }
                fields.append(NetLiftV5PeerField(
                    id: id,
                    kind: kind,
                    nullable: parts[2] == "1",
                    optional: parts[3] == "1"))
            } else {
                values[key] = value
            }
        }
        self.values = values
        self.fields = fields

        switch mutation {
        case .none:
            break
        case .inactive:
            active = false
        case .invalidLocalHash:
            self.values["localSchemaHash"] = "invalid"
        case .wrongPeerHash:
            self.values["schemaHash"] = String(repeating: "0", count: 64)
            self.values["peerSchemaHash"] = String(repeating: "0", count: 64)
        case .wrongProfile:
            self.values["profileId"] = "2"
        case .wrongAbi:
            self.values["abiMajor"] = "2"
        case .wrongFeatures:
            self.values["featureBits"] = "16"
            self.values["requiredFeatureBits"] = "16"
        case .wrongContract:
            self.values["contractMinor"] = "999"
        case .wrongService:
            self.values["serviceID"] = "99"
        case .wrongMethod:
            self.values["methodID"] = "99"
        case .wrongDto:
            self.values["dtoID"] = "99"
            self.values["resultDTOID"] = "99"
        case .wrongDtoVersion:
            self.values["dtoVersion"] = "2"
            self.values["resultDTOVersion"] = "2"
        case .unknownRequiredField:
            self.fields.append(NetLiftV5PeerField(
                id: 99, kind: .utf8, nullable: false, optional: false))
        case .incompatibleKind:
            guard let index = self.fields.firstIndex(where: { $0.id == 1 }) else {
                throw AsyncHostContextError.malformed
            }
            self.fields[index] = NetLiftV5PeerField(
                id: 1, kind: .bool, nullable: false, optional: false)
        case .duplicateField:
            guard let first = self.fields.first else { throw AsyncHostContextError.malformed }
            self.fields.append(first)
        case .missingRequiredField:
            self.fields.removeAll(where: { $0.id == 1 })
        }
    }

    func text(_ key: String) throws -> String {
        guard let value = values[key] else { throw AsyncHostContextError.malformed }
        return value
    }

    func number(_ key: String) throws -> UInt32 {
        guard let value = values[key], let parsed = UInt32(value) else {
            throw AsyncHostContextError.malformed
        }
        return parsed
    }
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

    private init(fixture: AsyncHostContextFixture) throws {
        abiMajor = try fixture.number("abiMajor")
        schemaVersion = try fixture.number("schemaVersion")
        contractMajor = try fixture.number("contractMajor")
        contractMinor = try fixture.number("contractMinor")
        featureBits = try fixture.number("featureBits")
        requiredFeatureBits = try fixture.number("requiredFeatureBits")
        serviceID = try fixture.number("serviceID")
        methodID = try fixture.number("methodID")
        dtoID = try fixture.number("dtoID")
        dtoVersion = try fixture.number("dtoVersion")
        localSchemaHash = try fixture.text("localSchemaHash")
        schemaHash = try fixture.text("schemaHash")
        fields = fixture.fields
        active = fixture.active
    }

    public func requireActive() throws {
        guard active else { throw AsyncHostContextError.inactive }
    }

    public func revokeForTesting() {
        active = false
    }

    public static func testingOnly(
        path: String,
        mutation: AsyncHostContextTestMutation = .none
    ) throws -> NetLiftV5PeerContext {
        try NetLiftV5PeerContext(fixture: AsyncHostContextFixture(path: path, mutation: mutation))
    }
}

public final class ManagedResultSchemaContext {
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
    private var active: Bool

    private init(fixture: AsyncHostContextFixture) throws {
        abiMajor = try fixture.number("abiMajor")
        schemaVersion = try fixture.number("schemaVersion")
        contractMajor = try fixture.number("contractMajor")
        contractMinor = try fixture.number("contractMinor")
        featureBits = try fixture.number("featureBits")
        requiredFeatureBits = try fixture.number("requiredFeatureBits")
        profileId = try fixture.number("profileId")
        serviceID = try fixture.number("serviceID")
        methodID = try fixture.number("methodID")
        resultDTOID = try fixture.number("resultDTOID")
        resultDTOVersion = try fixture.number("resultDTOVersion")
        localSchemaHash = try fixture.text("localSchemaHash")
        peerSchemaHash = try fixture.text("peerSchemaHash")
        fields = fixture.fields
        active = fixture.active
    }

    public func requireActive() throws {
        guard active else { throw AsyncHostContextError.inactive }
    }

    public func revokeForTesting() {
        active = false
    }

    public static func testingOnly(
        path: String,
        mutation: AsyncHostContextTestMutation = .none
    ) throws -> ManagedResultSchemaContext {
        try ManagedResultSchemaContext(fixture: AsyncHostContextFixture(path: path, mutation: mutation))
    }
}
