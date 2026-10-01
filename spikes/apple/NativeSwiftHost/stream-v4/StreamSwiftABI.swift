import Foundation
import os

struct NativeStreamV4ABI: LiftStreamABI {
    private let onActivated: @Sendable () -> Void

    init(onActivated: @escaping @Sendable () -> Void = {}) {
        self.onActivated = onActivated
    }

    func start(abiVersion: UInt32, schemaHash: [UInt8]) throws -> LiftStreamRuntimeInfo {
        guard schemaHash.count == 32 else {
            throw LiftStreamFailure.protocolError("Stream schema hash must contain 32 bytes.")
        }

        var runtimeAbiVersion: UInt32 = 0
        var featureBits: UInt32 = 0
        var runtimeSchemaHash = [UInt8](repeating: 0, count: 32)
        let status = schemaHash.withUnsafeBufferPointer { schema in
            runtimeSchemaHash.withUnsafeMutableBufferPointer { runtimeHash in
                netlift_stream_v4_start(
                    abiVersion,
                    schema.baseAddress,
                    UInt32(schema.count),
                    &runtimeAbiVersion,
                    &featureBits,
                    runtimeHash.baseAddress,
                    UInt32(runtimeHash.count))
            }
        }
        guard status == 0 else {
            throw LiftStreamFailure.protocolError("Stream runtime start failed with status \(status).")
        }
        return LiftStreamRuntimeInfo(
            abiVersion: runtimeAbiVersion,
            featureBits: featureBits,
            schemaHash: runtimeSchemaHash)
    }

    func subscribe(
        serviceID: UInt32,
        subscriptionID: UInt32,
        callbacks: LiftStreamCallbacks,
        initialCredit: UInt32,
        transportCapacity: UInt32,
        maxItemBytes: UInt32
    ) throws -> UInt64 {
        var token: UInt64 = 0
        let status = netlift_stream_v4_subscribe(
            serviceID,
            subscriptionID,
            initialCredit,
            transportCapacity,
            maxItemBytes,
            { token, bytes, length in
                let payload = bytes.map { Data(bytes: $0, count: Int(length)) } ?? Data()
                callbacks.onNext(token, payload)
            },
            { token, terminal, status, bytes, length in
                let diagnostic = bytes.map { Data(bytes: $0, count: Int(length)) } ?? Data()
                let swiftStatus: UInt32
                switch terminal {
                case 0: swiftStatus = 0
                case 1: swiftStatus = 1
                default: swiftStatus = status == 0 ? terminal : status
                }
                callbacks.onTerminal(token, swiftStatus, diagnostic)
            },
            { token in callbacks.onRelease(token) },
            &token)
        guard status == 0, token != 0 else {
            throw LiftStreamFailure.protocolError("Stream subscribe failed with status \(status).")
        }
        return token
    }

    func activate(_ token: UInt64) throws {
        let status = netlift_stream_v4_activate(token)
        guard status == 0 else {
            throw LiftStreamFailure.protocolError("Stream activation failed with status \(status).")
        }
        onActivated()
    }

    func request(_ token: UInt64, credits: UInt32) throws {
        let status = netlift_stream_v4_request(token, credits)
        guard status == 0 else {
            throw LiftStreamFailure.protocolError("Stream request failed with status \(status).")
        }
    }

    func unsubscribe(_ token: UInt64) {
        let status = netlift_stream_v4_unsubscribe(token)
        if status != 0 {
            os_log(.error, "Stream unsubscribe for token %llu failed with status %u.", token, status)
        }
    }
}
