import Foundation
import NetLiftNativeHostABI

private final class TypedReplyResult: @unchecked Sendable
{
    private let lock = NSLock()
    private let completed = DispatchSemaphore(value: 0)
    private var value: OrderReceipt?
    private var failure: Error?

    func finish(_ value: OrderReceipt?, _ failure: Error?)
    {
        lock.lock()
        self.value = value
        self.failure = failure
        lock.unlock()
        completed.signal()
    }

    func wait() -> (OrderReceipt?, Error?)
    {
        guard completed.wait(timeout: .now() + 10) == .success else
        {
            return (nil, NetLiftABIError(status: 11, message: "Timed out awaiting typed Swift client result."))
        }
        lock.lock()
        defer { lock.unlock() }
        return (value, failure)
    }
}

private final class TypedReplyFunctions: @unchecked Sendable
{
    let pointer: UnsafePointer<lift_function_table>

    init(_ pointer: UnsafePointer<lift_function_table>)
    {
        self.pointer = pointer
    }
}

@_cdecl("netlift_swift_run_typed_tests")
public func netLiftSwiftRunTypedTests(_ functions: UnsafePointer<lift_function_table>?) -> Int32
{
    guard let functions else { return 1 }
    let sharedFunctions = TypedReplyFunctions(functions)
    let result = TypedReplyResult()
    Task
    {
        do
        {
            let client = try NetLiftAsyncClient_2(
                functions: sharedFunctions.pointer,
                replyTyped: { registration, request, reply in
                    var nativeReply = lift_native_reply()
                    nativeReply.struct_size = UInt32(MemoryLayout<lift_native_reply>.size)
                    nativeReply.protocol_version = 1
                    nativeReply.outcome = reply.outcome
                    nativeReply.accepted = reply.accepted ? 1 : 0
                    let status = lift_status()
                    let code = reply.diagnostic.withUnsafeBufferPointer { diagnostic in
                        nativeReply.diagnostic = lift_bytes_view(
                            data: diagnostic.baseAddress,
                            length: UInt32(diagnostic.count))
                        var mutableStatus = status
                        return netlift_typed_reply(registration, request, &nativeReply, &mutableStatus)
                    }
                    guard code == 0 else
                    {
                        throw NetLiftABIError(status: code, message: "ReplyTyped rejected generated Swift reply.")
                    }
                },
                confirm: { prompt in
                    if prompt == "Confirm order SKU-FAIL x 3"
                    {
                        throw NetLiftABIError(status: 13, message: "Swift confirmation handler failed.")
                    }
                    if prompt == "Confirm order SKU-CANCEL x 3" { throw CancellationError() }
                    guard prompt == "Confirm order SKU-41 x 3" else
                    {
                        throw NetLiftABIError(status: 7, message: "Generated Swift client received an unexpected prompt.")
                    }
                    return true
                })
            let receipt = try await client.placeAsync(draft: OrderDraft(sku: "SKU-41", quantity: 3))
            guard receipt.id == 41 && receipt.sku == "SKU-41" && receipt.quantity == 3 else
            {
                throw NetLiftABIError(status: 7, message: "Generated Swift typed reply receipt mismatch.")
            }
            do
            {
                _ = try await client.placeAsync(draft: OrderDraft(sku: "SKU-FAIL", quantity: 3))
                throw NetLiftABIError(status: 7, message: "Typed handler failure was returned as success.")
            }
            catch let error as NetLiftABIError where error.status == 13 { }
            do
            {
                _ = try await client.placeAsync(draft: OrderDraft(sku: "SKU-CANCEL", quantity: 3))
                throw NetLiftABIError(status: 7, message: "Typed cancellation was returned as success.")
            }
            catch is CancellationError { }
            try await client.close()
            result.finish(receipt, nil)
        }
        catch
        {
            result.finish(nil, error)
        }
    }

    let (receipt, failure) = result.wait()
    if let failure
    {
        fputs("FAIL generated typed Swift facade: \(failure)\n", stderr)
        return 2
    }
    guard let receipt,
          receipt.id == 41,
          receipt.sku == "SKU-41",
          receipt.quantity == 3 else { return 3 }
    print("PASS generated Swift schema-v3 client, typed accepted reply and drain")
    return 0
}
