import Foundation
import NetLiftNativeHostABI

private enum SwiftHostTestError: Error
{
    case failed(String)
}

private final class FunctionTableReference: @unchecked Sendable
{
    let value: UnsafePointer<lift_function_table>
    init(_ value: UnsafePointer<lift_function_table>) { self.value = value }
}

private final class SwiftHostTestOutcome: @unchecked Sendable
{
    private let lock = NSLock()
    private var result: Int32 = -1
    func set(_ value: Int32) { lock.lock(); result = value; lock.unlock() }
    var value: Int32 { lock.lock(); defer { lock.unlock() }; return result }
}

private final class SwiftHostPrompt: @unchecked Sendable
{
    private let lock = NSLock()
    private var stored = ""
    func set(_ value: String) { lock.lock(); stored = value; lock.unlock() }
    var value: String { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class SwiftHostGate: @unchecked Sendable
{
    let entered = DispatchSemaphore(value: 0)
    let release = DispatchSemaphore(value: 0)
    private let prompt = SwiftHostPrompt()
    func confirm(_ value: String) -> Bool
    {
        prompt.set(value)
        entered.signal()
        release.wait()
        return true
    }
    var lastPrompt: String { prompt.value }
}

private final class SwiftHostFunctionTable: @unchecked Sendable
{
    let pointer: UnsafeMutablePointer<lift_function_table>
    init(_ source: UnsafePointer<lift_function_table>)
    {
        pointer = .allocate(capacity: 1)
        pointer.initialize(to: source.pointee)
    }
    deinit
    {
        pointer.deinitialize(count: 1)
        pointer.deallocate()
    }
}

private final class SwiftHostBeginPublicationRace: @unchecked Sendable
{
    let entered = DispatchSemaphore(value: 0)
    let proceed = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var original: lift_begin_fn?
    private var task: Task<OrderReceipt, Error>?

    func waitBeforeCall()
    {
        entered.signal()
        proceed.wait()
    }

    func install(original: lift_begin_fn, task: Task<OrderReceipt, Error>)
    {
        lock.lock()
        self.original = original
        self.task = task
        lock.unlock()
    }

    func invoke(
        _ service: UInt32,
        _ method: UInt32,
        _ registration: UInt64,
        _ arguments: UnsafePointer<lift_bytes_view>?,
        _ operation: UnsafeMutablePointer<UInt64>?,
        _ status: UnsafeMutablePointer<lift_status>?) -> UInt32
    {
        lock.lock()
        let original = self.original
        let task = self.task
        lock.unlock()
        guard let original else { return 1 }
        let result = original(service, method, registration, arguments, operation, status)
        task?.cancel()
        return result
    }
}

private let swiftHostBeginPublicationRace = SwiftHostBeginPublicationRace()

private final class SwiftHostFailedBegin: @unchecked Sendable
{
    private let lock = NSLock()
    private var originalBegin: lift_begin_fn?
    private var originalCancel: lift_cancel_fn?
    private var cancelCalls = 0

    func install(begin: lift_begin_fn, cancel: lift_cancel_fn)
    {
        lock.lock()
        originalBegin = begin
        originalCancel = cancel
        lock.unlock()
    }

    func invoke(
        _ service: UInt32,
        _ method: UInt32,
        _ registration: UInt64,
        _ arguments: UnsafePointer<lift_bytes_view>?,
        _ operation: UnsafeMutablePointer<UInt64>?,
        _ status: UnsafeMutablePointer<lift_status>?) -> UInt32
    {
        lock.lock()
        let begin = originalBegin
        lock.unlock()
        guard let begin else { return 1 }
        let result = begin(service, method, registration, arguments, operation, status)
        if result == 0
        {
            status?.pointee.code = 1
            return 1
        }
        return result
    }

    func cancel(_ operation: UInt64, _ status: UnsafeMutablePointer<lift_status>?) -> UInt32
    {
        lock.lock()
        let cancel = originalCancel
        cancelCalls += 1
        lock.unlock()
        return cancel?(operation, status) ?? 1
    }

    var cancelCallCount: Int { lock.lock(); defer { lock.unlock() }; return cancelCalls }
}

private let swiftHostFailedBegin = SwiftHostFailedBegin()

@_cdecl("netlift_swift_test_failed_begin")
private func netliftSwiftTestFailedBegin(
    _ service: UInt32,
    _ method: UInt32,
    _ registration: UInt64,
    _ arguments: UnsafePointer<lift_bytes_view>?,
    _ operation: UnsafeMutablePointer<UInt64>?,
    _ status: UnsafeMutablePointer<lift_status>?) -> UInt32
{
    swiftHostFailedBegin.invoke(service, method, registration, arguments, operation, status)
}

@_cdecl("netlift_swift_test_failed_begin_cancel")
private func netliftSwiftTestFailedBeginCancel(
    _ operation: UInt64,
    _ status: UnsafeMutablePointer<lift_status>?) -> UInt32
{
    swiftHostFailedBegin.cancel(operation, status)
}

@_cdecl("netlift_swift_test_begin_publication_race")
private func netliftSwiftTestBeginPublicationRace(
    _ service: UInt32,
    _ method: UInt32,
    _ registration: UInt64,
    _ arguments: UnsafePointer<lift_bytes_view>?,
    _ operation: UnsafeMutablePointer<UInt64>?,
    _ status: UnsafeMutablePointer<lift_status>?) -> UInt32
{
    swiftHostBeginPublicationRace.invoke(service, method, registration, arguments, operation, status)
}

private func require(_ condition: @autoclosure () -> Bool, _ message: String) throws
{
    if !condition() { throw SwiftHostTestError.failed(message) }
}

private func runSwiftAsyncFacadeTests(_ functions: UnsafePointer<lift_function_table>) async throws
{
    let draft = OrderDraft(sku: "SKU-41", quantity: 3)
    let expectedPrompt = "Confirm order SKU-41 x 3"

    let acceptedPrompt = SwiftHostPrompt()
    let acceptedClient = try NetLiftAsyncClient_2(functions: functions) { prompt in
        acceptedPrompt.set(prompt)
        return prompt == expectedPrompt
    }
    let receipt = try await acceptedClient.placeAsync(draft: draft)
    try require(receipt.id == 41 && receipt.sku == draft.sku && receipt.quantity == draft.quantity,
                "generated Swift facade decoded an unexpected OrderReceipt")
    try require(acceptedPrompt.value == expectedPrompt, "generated Swift facade decoded an unexpected confirmation prompt")
    try await acceptedClient.close()
    let acceptedEvents = acceptedClient.callbackEvents
    guard let requestIndex = acceptedEvents.firstIndex(of: "request-start"),
          let resultIndex = acceptedEvents.firstIndex(of: "result"),
          let requestEndIndex = acceptedEvents.firstIndex(of: "request-end"),
          let releaseIndex = acceptedEvents.firstIndex(of: "release") else {
        throw SwiftHostTestError.failed("accepted facade did not record the complete callback lifecycle")
    }
    try require(requestIndex < resultIndex && requestEndIndex < releaseIndex && resultIndex < releaseIndex,
                "accepted facade callbacks violated request/result/release ordering")
    print("PASS generated Swift facade accepted Orders call and callback ordering")

    let rejectedClient = try NetLiftAsyncClient_2(functions: functions) { prompt in
        prompt == expectedPrompt ? false : true
    }
    do
    {
        _ = try await rejectedClient.placeAsync(draft: draft)
        throw SwiftHostTestError.failed("managed rejection was returned as a successful OrderReceipt")
    }
    catch let error as NetLiftABIError
    {
        try require(error.status == 8 && error.message == "Managed order operation failed.",
                    "generated Swift facade did not preserve the managed error status/message")
    }
    try await rejectedClient.close()
    print("PASS generated Swift facade managed error propagation")

    let table = SwiftHostFunctionTable(functions)
    guard let originalBegin = table.pointer.pointee.begin else {
        throw SwiftHostTestError.failed("native host omitted begin function")
    }
    table.pointer.pointee.begin = netliftSwiftTestBeginPublicationRace
    let publicationClient = try NetLiftAsyncClient_2(functions: UnsafePointer(table.pointer)) { _ in true }
    let publicationTask = Task {
        swiftHostBeginPublicationRace.waitBeforeCall()
        return try await publicationClient.placeAsync(draft: draft)
    }
    try require(swiftHostBeginPublicationRace.entered.wait(timeout: .now() + 10) == .success,
                "pre-publication cancellation task did not reach its deterministic gate")
    swiftHostBeginPublicationRace.install(original: originalBegin, task: publicationTask)
    swiftHostBeginPublicationRace.proceed.signal()
    do
    {
        _ = try await publicationTask.value
        throw SwiftHostTestError.failed("pre-publication cancellation returned a successful receipt")
    }
    catch is CancellationError
    {
    }
    try await publicationClient.close()
    let publicationEvents = publicationClient.callbackEvents
    try require(publicationEvents.filter { $0 == "release" }.count == 1 &&
                !publicationEvents.contains("request-start") &&
                !publicationEvents.contains("result") &&
                !publicationEvents.contains("unexpected-result"),
                "cancellation before token publication activated or completed a provisional operation")
    print("PASS generated Swift facade cancellation at token publication")

    let failedBeginTable = SwiftHostFunctionTable(functions)
    guard let failedBegin = failedBeginTable.pointer.pointee.begin,
          let failedBeginCancel = failedBeginTable.pointer.pointee.cancel else {
        throw SwiftHostTestError.failed("native host omitted begin or cancel function")
    }
    swiftHostFailedBegin.install(begin: failedBegin, cancel: failedBeginCancel)
    failedBeginTable.pointer.pointee.begin = netliftSwiftTestFailedBegin
    failedBeginTable.pointer.pointee.cancel = netliftSwiftTestFailedBeginCancel
    let failedBeginClient = try NetLiftAsyncClient_2(functions: UnsafePointer(failedBeginTable.pointer)) { _ in true }
    do
    {
        _ = try await failedBeginClient.placeAsync(draft: draft)
        throw SwiftHostTestError.failed("failed begin with an allocated token returned a receipt")
    }
    catch let error as NetLiftABIError
    {
        try require(error.status == 1, "failed begin returned the wrong ABI error status")
    }
    try require(swiftHostFailedBegin.cancelCallCount == 1,
                "failed begin did not cancel its unpublished provisional token exactly once")
    try await failedBeginClient.close()
    let failedBeginEvents = failedBeginClient.callbackEvents
    try require(failedBeginEvents.filter { $0 == "release" }.count == 1 &&
                !failedBeginEvents.contains("request-start") &&
                !failedBeginEvents.contains("result"),
                "failed begin cleanup activated or completed the provisional operation")
    print("PASS generated Swift facade failed-begin token cleanup")

    let gate = SwiftHostGate()
    let cancelledClient = try NetLiftAsyncClient_2(functions: functions) { prompt in
        gate.confirm(prompt)
    }
    let orderTask = Task { try await cancelledClient.placeAsync(draft: draft) }
    try require(gate.entered.wait(timeout: .now() + 10) == .success,
                "native confirmation callback did not enter for cancellation test")
    try require(gate.lastPrompt == expectedPrompt, "cancellation route received a malformed prompt")
    orderTask.cancel()
    do
    {
        _ = try await orderTask.value
        throw SwiftHostTestError.failed("cancelled generated Swift facade returned a successful receipt")
    }
    catch is CancellationError
    {
    }
    try require(!cancelledClient.didRelease, "native context released while Swift confirmation callback was blocked")
    let closeTask = Task { try await cancelledClient.close() }
    try await Task.sleep(for: .milliseconds(50))
    try require(!cancelledClient.didRelease, "OnRelease did not wait for Swift callback return")
    gate.release.signal()
    try await closeTask.value
    let cancelledEvents = cancelledClient.callbackEvents
    guard let cancelRequestIndex = cancelledEvents.firstIndex(of: "request-start"),
          let cancelResultIndex = cancelledEvents.firstIndex(of: "result"),
          let cancelRequestEndIndex = cancelledEvents.firstIndex(of: "request-end"),
          let cancelReleaseIndex = cancelledEvents.firstIndex(of: "release") else {
        throw SwiftHostTestError.failed("cancelled facade did not record the complete callback lifecycle")
    }
    try require(cancelRequestIndex < cancelResultIndex &&
                cancelResultIndex < cancelRequestEndIndex &&
                cancelRequestEndIndex < cancelReleaseIndex,
                "cancellation/release callback ordering was not preserved")
    try require(cancelledEvents.filter { $0 == "result" }.count == 1 &&
                !cancelledEvents.contains("unexpected-result"),
                "synchronous cancel completion did not produce exactly one terminal callback")
    print("PASS generated Swift facade cancellation, late reply and callback drain")
}

@_cdecl("netlift_swift_run_async_tests")
public func netliftSwiftRunAsyncTests(_ functions: UnsafePointer<lift_function_table>?) -> Int32
{
    guard let functions else { return -1 }
    let table = FunctionTableReference(functions)
    let outcome = SwiftHostTestOutcome()
    let finished = DispatchSemaphore(value: 0)
    Task.detached
    {
        do
        {
            try await runSwiftAsyncFacadeTests(table.value)
            outcome.set(0)
        }
        catch
        {
            fputs("FAIL generated Swift facade: \(error)\n", stderr)
            outcome.set(1)
        }
        finished.signal()
    }
    guard finished.wait(timeout: .now() + 60) == .success else
    {
        fputs("FAIL generated Swift facade timed out\n", stderr)
        return -2
    }
    return outcome.value
}
