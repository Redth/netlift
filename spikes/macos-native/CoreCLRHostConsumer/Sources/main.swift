import CoreCLRHost
import CoreCLRHostABI
import Dispatch
import Darwin
import Foundation

enum ConsumerError: LocalizedError {
    case usage
    case unexpectedRuntimeVersion(Int32)
    case lifecycleRace(String)
    case duplicateReleaseAccepted
    case releasedHandleAccepted
    case handleIdentifierReused

    var errorDescription: String? {
        switch self {
        case .usage:
            return "Expected the managed output directory."
        case .unexpectedRuntimeVersion(let major):
            return "Expected .NET 11, got .NET \(major)."
        case .lifecycleRace(let details):
            return "Concurrent CoreCLR handle lifecycle failed: \(details)"
        case .duplicateReleaseAccepted:
            return "A duplicate release was not rejected as stale."
        case .releasedHandleAccepted:
            return "A released handle was accepted after a new handle opened."
        case .handleIdentifierReused:
            return "A new host reused a previously issued identifier."
        }
    }
}

@main
struct CoreCLRHostConsumer {
    static func main() {
        do {
            try run()
        } catch {
            fputs("CoreCLRHost consumer failed: \(error)\n", stderr)
            exit(1)
        }
    }

    private static func run() throws {
        guard CommandLine.arguments.count == 2 else {
            throw ConsumerError.usage
        }

        let managedDirectory = URL(fileURLWithPath: CommandLine.arguments[1])
        let host = try CoreCLRHost(
            runtimeConfigURL: managedDirectory
                .appendingPathComponent("NetLift.MacBridge.runtimeconfig.json"))
        let entryPoint = try host.loadUnmanagedCallersOnlyMethod(
            assemblyURL: managedDirectory.appendingPathComponent("NetLift.MacBridge.dll"),
            typeName: "NetLift.Spikes.MacOS.HostBridge, NetLift.MacBridge",
            methodName: "GetRuntimeMajor")
        typealias GetRuntimeMajor = @convention(c) () -> Int32
        let getRuntimeMajor = unsafeBitCast(entryPoint, to: GetRuntimeMajor.self)
        let major = getRuntimeMajor()
        guard major == 11 else {
            throw ConsumerError.unexpectedRuntimeVersion(major)
        }
        for _ in 0..<32 {
            try verifyConcurrentHandleLifecycle(managedDirectory: managedDirectory)
        }
        print("PASS independent SwiftPM consumer loaded managed .NET \(major) probe")
        print("PASS 32 concurrent CoreCLR handle load/release and stale-handle races")
    }

    private static func verifyConcurrentHandleLifecycle(
        managedDirectory: URL) throws
    {
        let runtimeConfigURL =
            managedDirectory.appendingPathComponent("NetLift.MacBridge.runtimeconfig.json")
        let assemblyPath =
            managedDirectory.appendingPathComponent("NetLift.MacBridge.dll").path
        let typeName = "NetLift.Spikes.MacOS.HostBridge, NetLift.MacBridge"
        let methodName = "GetRuntimeMajor"
        var errorBuffer = [CChar](repeating: 0, count: 1024)
        var host: netlift_coreclr_host = 0
        let openStatus = runtimeConfigURL.path.withCString { configPath in
            netlift_coreclr_host_open(
                configPath,
                &host,
                &errorBuffer,
                errorBuffer.count)
        }
        guard openStatus == 0, host != 0 else {
            throw CoreCLRHostError(message: String(cString: errorBuffer))
        }

        let racedHost = host
        let ready = DispatchGroup()
        let finished = DispatchGroup()
        let start = DispatchSemaphore(value: 0)
        let results = LifecycleRaceResults()
        let workerCount = 16

        for worker in 0..<workerCount {
            ready.enter()
            finished.enter()
            DispatchQueue.global().async {
                ready.leave()
                start.wait()

                var errorBuffer = [CChar](repeating: 0, count: 1024)
                if worker == 0 {
                    let status = netlift_coreclr_host_release(
                        racedHost,
                        &errorBuffer,
                        errorBuffer.count)
                    results.recordRelease(
                        status: status,
                        message: String(cString: errorBuffer))
                } else {
                    var entryPoint: UnsafeMutableRawPointer?
                    let status = assemblyPath.withCString { assembly in
                        typeName.withCString { type in
                            methodName.withCString { method in
                                netlift_coreclr_host_load_unmanaged_callers_only(
                                    racedHost,
                                    assembly,
                                    type,
                                    method,
                                    &entryPoint,
                                    &errorBuffer,
                                    errorBuffer.count)
                            }
                        }
                    }
                    results.recordLoad(
                        status: status,
                        entryPoint: entryPoint,
                        message: String(cString: errorBuffer))
                }
                finished.leave()
            }
        }

        ready.wait()
        for _ in 0..<workerCount {
            start.signal()
        }
        finished.wait()
        guard results.hasExpectedOutcomes(loadCount: workerCount - 1) else {
            throw ConsumerError.lifecycleRace(results.summary)
        }

        errorBuffer = [CChar](repeating: 0, count: 1024)
        let duplicateReleaseStatus = netlift_coreclr_host_release(
            racedHost,
            &errorBuffer,
            errorBuffer.count)
        guard duplicateReleaseStatus != 0,
              String(cString: errorBuffer).contains("stale or invalid") else {
            throw ConsumerError.duplicateReleaseAccepted
        }

        errorBuffer = [CChar](repeating: 0, count: 1024)
        var replacementHost: netlift_coreclr_host = 0
        let replacementStatus = runtimeConfigURL.path.withCString { configPath in
            netlift_coreclr_host_open(
                configPath,
                &replacementHost,
                &errorBuffer,
                errorBuffer.count)
        }
        guard replacementStatus == 0, replacementHost != 0 else {
            throw CoreCLRHostError(message: String(cString: errorBuffer))
        }
        guard replacementHost != racedHost else {
            throw ConsumerError.handleIdentifierReused
        }

        var staleEntryPoint: UnsafeMutableRawPointer?
        errorBuffer = [CChar](repeating: 0, count: 1024)
        let staleStatus = assemblyPath.withCString { assembly in
            typeName.withCString { type in
                methodName.withCString { method in
                    netlift_coreclr_host_load_unmanaged_callers_only(
                        racedHost,
                        assembly,
                        type,
                        method,
                        &staleEntryPoint,
                        &errorBuffer,
                        errorBuffer.count)
                }
            }
        }
        guard staleStatus != 0, staleEntryPoint == nil,
              String(cString: errorBuffer).contains("stale or invalid") else {
            throw ConsumerError.releasedHandleAccepted
        }

        errorBuffer = [CChar](repeating: 0, count: 1024)
        let replacementReleaseStatus = netlift_coreclr_host_release(
            replacementHost,
            &errorBuffer,
            errorBuffer.count)
        guard replacementReleaseStatus == 0 else {
            throw CoreCLRHostError(message: String(cString: errorBuffer))
        }
    }
}

private final class LifecycleRaceResults: @unchecked Sendable {
    private let lock = NSLock()
    private var successfulLoads = 0
    private var staleLoads = 0
    private var successfulReleases = 0
    private var failures: [String] = []

    func recordLoad(
        status: Int32,
        entryPoint: UnsafeMutableRawPointer?,
        message: String)
    {
        lock.lock()
        defer { lock.unlock() }

        if status == 0, entryPoint != nil {
            successfulLoads += 1
        } else if status != 0, message.contains("stale or invalid") {
            staleLoads += 1
        } else {
            failures.append("load status \(status): \(message)")
        }
    }

    func recordRelease(status: Int32, message: String) {
        lock.lock()
        defer { lock.unlock() }

        if status == 0 {
            successfulReleases += 1
        } else {
            failures.append("release status \(status): \(message)")
        }
    }

    func hasExpectedOutcomes(loadCount: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return successfulReleases == 1 &&
            successfulLoads + staleLoads == loadCount &&
            failures.isEmpty
    }

    var summary: String {
        lock.lock()
        defer { lock.unlock() }
        return "loads=\(successfulLoads) stale=\(staleLoads) releases=\(successfulReleases); " +
            failures.joined(separator: "; ")
    }
}
