import NetLiftC

public enum NetLiftRuntimeError: Error, Equatable {
    case incompatibleABI(status: UInt32, requiredSize: UInt32)
}

public enum NetLiftRuntime {
    public static let abiVersion: UInt32 = UInt32(NETLIFT_ABI_VERSION_1)

    public static func validateABI() throws {
        var api = netlift_api_v1()
        var requiredSize: UInt32 = 0
        let status = netlift_get_api(
            abiVersion,
            UInt32(MemoryLayout<netlift_api_v1>.size),
            &api,
            &requiredSize
        )
        guard status == NETLIFT_STATUS_OK else {
            throw NetLiftRuntimeError.incompatibleABI(
                status: UInt32(status.rawValue),
                requiredSize: requiredSize
            )
        }
    }
}
