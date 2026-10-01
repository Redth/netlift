import Foundation
import NetLiftNativeHostABI

@_silgen_name("netlift_coreclr_get_function_table")
private func typedFunctionTable() -> UnsafePointer<lift_function_table>?

@_silgen_name("netlift_coreclr_complete_confirmation_typed")
private func typedReply(
    _ registration: UInt64,
    _ request: UInt64,
    _ reply: UnsafePointer<lift_native_reply>?) -> Int32

@_silgen_name("typed_stub_wait_for_reply")
private func typedStubWaitForReply(_ outcome: UInt32, _ timeoutMilliseconds: Int32) -> Int32

@_silgen_name("typed_stub_reset_reply")
private func typedStubResetReply()

@_silgen_name("typed_stub_last_outcome")
private func typedStubLastOutcome() -> UInt32

@_silgen_name("typed_stub_last_accepted")
private func typedStubLastAccepted() -> UInt32

@_silgen_name("typed_stub_copy_diagnostic")
private func typedStubCopyDiagnostic(_ destination: UnsafeMutablePointer<UInt8>?, _ capacity: UInt32) -> UInt32

private struct HandlerFailure: Error, CustomStringConvertible {
    var description: String { "typed confirmation handler failed" }
}

@main
private struct TypedReplyHostStubTest {
    static func main() async throws {
        guard let functions = typedFunctionTable() else {
            throw NetLiftABIError(status: 11, message: "The schema-v3 function table did not start.")
        }
        let client = try NetLiftAsyncClient_2(
            functions: functions,
            replyTyped: { registration, request, reply in
                var nativeReply = lift_native_reply()
                nativeReply.struct_size = UInt32(MemoryLayout<lift_native_reply>.size)
                nativeReply.protocol_version = 1
                nativeReply.outcome = reply.outcome
                nativeReply.accepted = reply.accepted ? 1 : 0
                let code = reply.diagnostic.withUnsafeBufferPointer { diagnostic in
                    nativeReply.diagnostic = lift_bytes_view(
                        data: diagnostic.isEmpty ? nil : diagnostic.baseAddress,
                        length: UInt32(diagnostic.count))
                    return withUnsafePointer(to: &nativeReply) {
                        typedReply(registration, request, $0)
                    }
                }
                guard code == 0 else {
                    throw NetLiftABIError(
                        status: UInt32(bitPattern: code),
                        message: "The typed host stub rejected ReplyTyped.")
                }
            },
            confirm: { prompt in
                switch prompt {
                case "ACCEPT":
                    return true
                case "DECLINE":
                    return false
                case "CANCEL":
                    throw CancellationError()
                case "HANDLER-FAILURE":
                    throw HandlerFailure()
                default:
                    throw NetLiftABIError(status: 7, message: "Unexpected confirmation prompt.")
                }
            })

        typedStubResetReply()
        let accepted = try await client.placeAsync(
            draft: OrderDraft(sku: "ACCEPT", quantity: 1))
        guard accepted.id == 42 && accepted.sku == "ACCEPT" && accepted.quantity == 1,
              typedStubWaitForReply(NetLiftNativeReplyOutcome.result, 5_000) == 0,
              typedStubLastAccepted() == 1 else {
            throw NetLiftABIError(status: 7, message: "Accepted typed reply did not complete correctly.")
        }

        typedStubResetReply()
        do {
            _ = try await client.placeAsync(draft: OrderDraft(sku: "DECLINE", quantity: 1))
            throw NetLiftABIError(status: 7, message: "Declined typed reply unexpectedly succeeded.")
        } catch let error as NetLiftABIError where error.status == 8 {
        }
        guard typedStubWaitForReply(NetLiftNativeReplyOutcome.result, 5_000) == 0,
              typedStubLastAccepted() == 0 else {
            throw NetLiftABIError(status: 7, message: "Decline was not emitted as RESULT(false).")
        }

        typedStubResetReply()
        do {
            _ = try await client.placeAsync(draft: OrderDraft(sku: "CANCEL", quantity: 1))
            throw NetLiftABIError(status: 7, message: "Cancelled typed reply unexpectedly succeeded.")
        } catch is CancellationError {
        }
        guard typedStubWaitForReply(NetLiftNativeReplyOutcome.cancelled, 5_000) == 0 else {
            throw NetLiftABIError(status: 7, message: "Cancellation was not emitted as CANCELLED.")
        }

        typedStubResetReply()
        let handlerFailure = Task {
            try await client.placeAsync(
                draft: OrderDraft(sku: "HANDLER-FAILURE", quantity: 1))
        }
        let handlerReplyWait = await Task.detached(priority: .userInitiated) {
            typedStubWaitForReply(NetLiftNativeReplyOutcome.handlerFailure, 5_000)
        }.value
        guard handlerReplyWait == 0 else {
            handlerFailure.cancel()
            throw NetLiftABIError(
                status: 7,
                message: "Handler failure was not emitted as HANDLER_FAILURE (wait=\(handlerReplyWait), captured=\(typedStubLastOutcome())).")
        }
        guard typedStubLastOutcome() == NetLiftNativeReplyOutcome.handlerFailure else {
            throw NetLiftABIError(status: 7, message: "The host stub did not capture HANDLER_FAILURE.")
        }
        var diagnostic = [UInt8](repeating: 0, count: 4_096)
        let diagnosticLength = diagnostic.withUnsafeMutableBufferPointer {
            typedStubCopyDiagnostic($0.baseAddress, UInt32($0.count))
        }
        guard diagnosticLength > 0 && diagnosticLength <= 4_096 else {
            throw NetLiftABIError(status: 7, message: "Handler diagnostic was empty or exceeded its bound.")
        }
        let capturedDiagnostic = String(
            decoding: diagnostic.prefix(Int(diagnosticLength)),
            as: UTF8.self)
        guard capturedDiagnostic == "typed confirmation handler failed" else {
            throw NetLiftABIError(status: 7, message: "The bounded handler diagnostic was not preserved.")
        }

        handlerFailure.cancel()
        do {
            _ = try await handlerFailure.value
            throw NetLiftABIError(status: 7, message: "The pending failure test did not drain by cancellation.")
        } catch is CancellationError {
        }

        try await client.close()
        guard client.didRelease else {
            throw NetLiftABIError(status: 7, message: "The typed client did not drain its callback registration.")
        }

        print("PASS schema-v3 accepted, declined, cancelled and handler-failure typed replies; bounded diagnostic captured; no managed status-13 claim.")
    }
}
