import NetLiftC
import Darwin
import Dispatch
import Foundation

public protocol NetLiftNativeServiceDispatcher: AnyObject, Sendable {
    func invoke(
        callToken: UInt64,
        serviceId: UInt32,
        memberId: UInt32,
        payload: Data
    ) throws -> Data

    func cancel(callToken: UInt64)
}

public final class NetLiftSynchronousCallbackResult<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private let semaphore = DispatchSemaphore(value: 0)
    private var result: Result<Value, any Error>?

    public init() {}

    public func complete(_ result: Result<Value, any Error>) {
        lock.lock()
        guard self.result == nil else {
            lock.unlock()
            return
        }
        self.result = result
        lock.unlock()
        semaphore.signal()
    }

    public func wait() throws -> Value {
        semaphore.wait()
        lock.lock()
        let result = self.result
        lock.unlock()
        guard let result else {
            throw NetLiftRuntimeError.callbackResultUnavailable
        }
        return try result.get()
    }
}

public struct NetLiftNativeServiceRegistration: Sendable {
    public let serviceId: UInt32
    public let ownerToken: UInt64
    public let token: UInt64
    public let generation: UInt64

    public init(serviceId: UInt32, ownerToken: UInt64, token: UInt64, generation: UInt64) {
        self.serviceId = serviceId
        self.ownerToken = ownerToken
        self.token = token
        self.generation = generation
    }
}

private final class NetLiftNativeServiceDispatcherBox: @unchecked Sendable {
    let dispatcher: any NetLiftNativeServiceDispatcher

    init(_ dispatcher: any NetLiftNativeServiceDispatcher) {
        self.dispatcher = dispatcher
    }
}

public typealias NetLiftNativeServiceInvokeFunction = @convention(c) (
    UnsafeMutableRawPointer?,
    UInt64,
    UInt32,
    UInt32,
    UnsafePointer<UInt8>?,
    Int,
    UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>?,
    UnsafeMutablePointer<Int>?
) -> Int32

public typealias NetLiftNativeServiceCancelFunction = @convention(c) (
    UnsafeMutableRawPointer?,
    UInt64
) -> Void

public typealias NetLiftNativeServiceReleaseFunction = @convention(c) (
    UnsafeMutableRawPointer?
) -> Void

@_cdecl("netlift_swift_native_service_invoke")
public func netLiftNativeServiceInvoke(
    context: UnsafeMutableRawPointer?,
    callToken: UInt64,
    serviceId: UInt32,
    memberId: UInt32,
    request: UnsafePointer<UInt8>?,
    requestLength: Int,
    response: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>?,
    responseLength: UnsafeMutablePointer<Int>?
) -> Int32 {
    guard let context,
          requestLength >= 0,
          (requestLength == 0 || request != nil),
          let response,
          let responseLength else {
        return Int32(NETLIFT_STATUS_INVALID_ARGUMENT.rawValue)
    }
    let dispatcher = Unmanaged<NetLiftNativeServiceDispatcherBox>
        .fromOpaque(context)
        .takeUnretainedValue()
        .dispatcher
    let payload = requestLength == 0
        ? Data()
        : Data(bytes: request!, count: requestLength)
    do {
        let bytes = try dispatcher.invoke(
            callToken: callToken,
            serviceId: serviceId,
            memberId: memberId,
            payload: payload
        )
        if !bytes.isEmpty {
            guard let memory = malloc(bytes.count) else {
                return Int32(NETLIFT_STATUS_OUT_OF_MEMORY.rawValue)
            }
            bytes.copyBytes(to: memory.assumingMemoryBound(to: UInt8.self), count: bytes.count)
            response.pointee = memory.assumingMemoryBound(to: UInt8.self)
        } else {
            response.pointee = nil
        }
        responseLength.pointee = bytes.count
        return Int32(NETLIFT_STATUS_OK.rawValue)
    } catch {
        return Int32(NETLIFT_STATUS_HANDLER_FAILURE.rawValue)
    }
}

@_cdecl("netlift_swift_native_service_cancel")
public func netLiftNativeServiceCancel(
    context: UnsafeMutableRawPointer?,
    callToken: UInt64
) {
    guard let context else { return }
    Unmanaged<NetLiftNativeServiceDispatcherBox>
        .fromOpaque(context)
        .takeUnretainedValue()
        .dispatcher
        .cancel(callToken: callToken)
}

@_cdecl("netlift_swift_native_service_release")
public func netLiftNativeServiceRelease(context: UnsafeMutableRawPointer?) {
    guard let context else { return }
    Unmanaged<NetLiftNativeServiceDispatcherBox>
        .fromOpaque(context)
        .release()
}

public func retainNativeServiceDispatcher(
    _ dispatcher: any NetLiftNativeServiceDispatcher
) -> UnsafeMutableRawPointer {
    Unmanaged.passRetained(NetLiftNativeServiceDispatcherBox(dispatcher)).toOpaque()
}

public func netLiftNativeServiceCallbackFunctions() -> (
    NetLiftNativeServiceInvokeFunction,
    NetLiftNativeServiceCancelFunction,
    NetLiftNativeServiceReleaseFunction
) {
    (
        netLiftNativeServiceInvoke,
        netLiftNativeServiceCancel,
        netLiftNativeServiceRelease
    )
}

public enum NetLiftRuntimeError: Error, Equatable {
    case incompatibleABI(status: UInt32, requiredSize: UInt32)
    case callbackResultUnavailable
}

public enum NetLiftRuntime {
    public static let abiVersion: UInt32 = UInt32(NETLIFT_ABI_VERSION_2)

    public static func validateABI() throws {
        var api = netlift_api_v2()
        var requiredSize: UInt32 = 0
        let status = netlift_get_api_v2(
            UInt32(MemoryLayout<netlift_api_v2>.size),
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
