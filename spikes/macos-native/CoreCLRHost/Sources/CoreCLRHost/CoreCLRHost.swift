import Foundation
import CoreCLRHostABI
import Darwin

public struct CoreCLRHostError: LocalizedError, Sendable {
    public let message: String

    public init(message: String) {
        self.message = message
    }

    public var errorDescription: String? { message }
}

public final class CoreCLRHost: @unchecked Sendable {
    private let handle: netlift_coreclr_host

    public init(runtimeConfigURL: URL) throws {
        var host: netlift_coreclr_host = 0
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = runtimeConfigURL.path.withCString { path in
            netlift_coreclr_host_open(
                path,
                &host,
                &errorBuffer,
                errorBuffer.count)
        }
        guard status == 0, host != 0 else {
            throw CoreCLRHostError(message: String(cString: errorBuffer))
        }
        handle = host
    }

    public func loadUnmanagedCallersOnlyMethod(
        assemblyURL: URL,
        typeName: String,
        methodName: String) throws -> UnsafeMutableRawPointer
    {
        var entryPoint: UnsafeMutableRawPointer?
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = assemblyURL.path.withCString { assemblyPath in
            typeName.withCString { typeName in
                methodName.withCString { methodName in
                    netlift_coreclr_host_load_unmanaged_callers_only(
                        handle,
                        assemblyPath,
                        typeName,
                        methodName,
                        &entryPoint,
                        &errorBuffer,
                        errorBuffer.count)
                }
            }
        }
        guard status == 0, let entryPoint else {
            throw CoreCLRHostError(message: String(cString: errorBuffer))
        }
        return entryPoint
    }

    deinit {
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        let status = netlift_coreclr_host_release(
            handle,
            &errorBuffer,
            errorBuffer.count)
        if status != 0 {
            fputs(
                "CoreCLRHost release failed: \(String(cString: errorBuffer))\n",
                stderr)
        }
    }
}
