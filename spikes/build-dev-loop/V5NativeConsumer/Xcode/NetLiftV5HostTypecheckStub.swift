public enum NetLiftV5PeerFieldKind: UInt8, Sendable {
    case utf8 = 1
    case i32 = 2
    case bool = 3
}

public struct NetLiftV5PeerField: Sendable {
    public let id: UInt16
    public let kind: NetLiftV5PeerFieldKind
    public let nullable: Bool
    public let optional: Bool
}

public struct NetLiftV5PeerContext: Sendable {
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

    public func requireActive() throws {}
}
