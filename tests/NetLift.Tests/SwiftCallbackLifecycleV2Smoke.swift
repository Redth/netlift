@_spi(NetLiftTesting) import NetLiftNativeHostABI
import Foundation

private enum SmokeFailure: Error
{
    case assertion(String)
}

private func require(_ condition: @autoclosure () -> Bool, _ message: String) throws
{
    if !condition() { throw SmokeFailure.assertion(message) }
}

private final class HandlerCounters: @unchecked Sendable
{
    private let lock = NSLock()
    private var calls = 0
    private var active = 0
    private var maximumActive = 0
    private var reentrantAwaitRejected = false

    func enter()
    {
        lock.lock()
        calls += 1
        active += 1
        maximumActive = max(maximumActive, active)
        lock.unlock()
    }

    func leave()
    {
        lock.lock()
        active -= 1
        lock.unlock()
    }

    func markReentrantAwaitRejected()
    {
        lock.lock()
        reentrantAwaitRejected = true
        lock.unlock()
    }

    var callCount: Int { lock.lock(); defer { lock.unlock() }; return calls }
    var maxActive: Int { lock.lock(); defer { lock.unlock() }; return maximumActive }
    var rejectedReentrantAwait: Bool
    {
        lock.lock()
        defer { lock.unlock() }
        return reentrantAwaitRejected
    }
}

private final class RegistrationBox: @unchecked Sendable
{
    private let lock = NSLock()
    private var value: NetLiftNativeCallbackRegistrationV2_1?

    func set(_ registration: NetLiftNativeCallbackRegistrationV2_1)
    {
        lock.lock()
        value = registration
        lock.unlock()
    }

    func get() -> NetLiftNativeCallbackRegistrationV2_1?
    {
        lock.lock()
        defer { lock.unlock() }
        return value
    }
}

private final class SuspendedHandler: @unchecked Sendable
{
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Never>?

    func suspend() async
    {
        await withCheckedContinuation { continuation in
            lock.lock()
            self.continuation = continuation
            lock.unlock()
        }
    }

    func resume()
    {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume()
    }

    var isSuspended: Bool
    {
        lock.lock()
        defer { lock.unlock() }
        return continuation != nil
    }
}

private struct TestConfirmation: Confirmation
{
    let invoke: @Sendable (String) async throws -> Bool
    func confirmAsync(prompt: String) async throws -> Bool { try await invoke(prompt) }
}

@main
private struct SwiftCallbackLifecycleV2Smoke
{
    static func main() async throws
    {
        try preflightRejectsProductionFallback()
        try await productionAccessorAndVersionPreflight()
        try productionAccessorFailuresAreClosed()
        try await acceptedReplyRetriesAndSerializesMainActorWork()
        try await malformedAndOversizedRequestsAreDistinct()
        try await revokeRetiresActiveAndQueuedRequests()
        try await replyAndRevokeClaimsAreExclusive()
        try await parentCancellationReturnsManagedRetirementReceipts()
        try await cancelRequestRejectsUnknownStaleAndRepliedTokens()
        try await parentCancellationFailureRetriesThroughRequestCancellation()
        try await permanentNativeFailuresAreObservableAndRecoverable()
        try await reentrantCloseDoesNotDeadlock()
        try await abandonedHandlerFailsClosedWithoutBlockingWaiters()
        try await stressTenThousandCloseCycles()
        print("PASS Swift callback lifecycle V2 fake ABI: preflight, retry, MainActor FIFO, cancellation/revoke, reentrancy, and 10,000 release cycles.")
    }

    private static func pointers() throws -> (UnsafePointer<lift_function_table>, UnsafePointer<lift_function_table_v2>)
    {
        guard let v1 = netlift_fake_v1_functions(),
              let v2 = netlift_fake_v2_functions() else {
            throw SmokeFailure.assertion("Fake ABI did not provide both function tables.")
        }
        return (v1, v2)
    }

    private static func preflightRejectsProductionFallback() throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY))
        let (v1, _) = try pointers()
        do {
            _ = try NetLiftNativeCallbackRegistrationV2_1(
                v1Functions: v1,
                handler: TestConfirmation { _ in true })
            throw SmokeFailure.assertion("Production V2 initializer accepted a host without feature 0x20.")
        } catch let error as NetLiftABIError {
            try require(error.status == 3, "Production feature preflight returned an unexpected error.")
        }
        try require(netlift_fake_accessor_calls() == 0 && netlift_fake_registration_calls() == 0,
                    "Missing feature 0x20 reached the V2 accessor or registration.")

        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS | LIFT_FEATURE_TYPED_NATIVE_REPLY))
        netlift_fake_set_table_flags(UInt32(NETLIFT_FAKE_MISSING_CANCEL_REQUEST))
        let (testV1, testV2) = try pointers()
        do {
            _ = try NetLiftNativeCallbackRegistrationV2_1(
                testingV2Functions: testV2,
                v1Functions: testV1,
                handler: TestConfirmation { _ in true })
            throw SmokeFailure.assertion("Debug injection accepted a table missing cancel_request.")
        } catch let error as NetLiftABIError {
            try require(error.status == 4, "Injected table validation returned an unexpected error.")
        }
        try require(netlift_fake_registration_calls() == 0,
                    "Invalid injected table reached callback registration.")

        for flag in [
            NETLIFT_FAKE_BAD_TABLE_VERSION,
            NETLIFT_FAKE_BAD_RESERVED,
            NETLIFT_FAKE_SHORT_TABLE,
            NETLIFT_FAKE_BAD_ABI
        ] {
            netlift_fake_reset()
            netlift_fake_set_table_flags(UInt32(flag))
            let (candidateV1, candidateV2) = try pointers()
            do {
                _ = try NetLiftNativeCallbackRegistrationV2_1(
                    testingV2Functions: candidateV2,
                    v1Functions: candidateV1,
                    handler: TestConfirmation { _ in true })
                throw SmokeFailure.assertion("Debug injection accepted an incompatible V2 table.")
            } catch let error as NetLiftABIError {
                try require(error.status == 4, "V2 table mismatch returned an unexpected status.")
            }
            try require(netlift_fake_registration_calls() == 0,
                        "Incompatible V2 table reached callback registration.")
        }

        netlift_fake_reset()
        netlift_fake_set_table_flags(UInt32(NETLIFT_FAKE_BAD_HASH))
        let (badHashV1, _) = try pointers()
        do {
            _ = try NetLiftNativeCallbackRegistrationV2_1(
                v1Functions: badHashV1,
                handler: TestConfirmation { _ in true })
            throw SmokeFailure.assertion("Production preflight accepted a mismatched schema hash.")
        } catch let error as NetLiftABIError {
            try require(error.status == 5, "Schema mismatch returned an unexpected status.")
        }
        try require(netlift_fake_accessor_calls() == 0 && netlift_fake_registration_calls() == 0,
                    "Schema mismatch reached the V2 accessor or registration.")
    }

    private static func productionAccessorAndVersionPreflight() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                        LIFT_FEATURE_TYPED_NATIVE_REPLY |
                                        LIFT_FEATURE_CALLBACK_LIFECYCLE_V2))
        let (v1, _) = try pointers()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            v1Functions: v1,
            handler: TestConfirmation { _ in true })
        try require(netlift_fake_accessor_calls() == 1 &&
                    netlift_fake_registration_calls() == 1,
                    "Production initializer did not use the versioned V2 accessor.")
        try require(netlift_fake_accessor_requested_version() ==
                        UInt32(LIFT_FUNCTION_TABLE_V2_VERSION) &&
                    netlift_fake_accessor_required_size() ==
                        UInt32(MemoryLayout<lift_function_table_v2>.size),
                    "Production initializer did not request the complete V2 table version/size.")
        registration.requestClose()
        try await registration.awaitClosed()
        try require(netlift_fake_ack_calls() == 1 && netlift_fake_release_calls() == 1,
                    "Production accessor registration did not drain and release.")
        try require(netlift_fake_ack_successes() == 1 &&
                    netlift_fake_ack_order() > 0 &&
                    netlift_fake_release_callback_order() > netlift_fake_ack_order(),
                    "Production close delivered release before a successful drain acknowledgement.")
    }

    private static func productionAccessorFailuresAreClosed() throws
    {
        let malformedAccessorFlags = [
            NETLIFT_FAKE_BAD_TABLE_VERSION,
            NETLIFT_FAKE_BAD_RESERVED,
            NETLIFT_FAKE_MISSING_CANCEL_REQUEST,
            NETLIFT_FAKE_SHORT_TABLE,
            NETLIFT_FAKE_BAD_ABI
        ]
        for flag in malformedAccessorFlags {
            netlift_fake_reset()
            netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                             LIFT_FEATURE_TYPED_NATIVE_REPLY |
                                             LIFT_FEATURE_CALLBACK_LIFECYCLE_V2))
            netlift_fake_set_table_flags(UInt32(flag))
            let (v1, _) = try pointers()
            do {
                _ = try NetLiftNativeCallbackRegistrationV2_1(
                    v1Functions: v1,
                    handler: TestConfirmation { _ in true })
                throw SmokeFailure.assertion("Production initializer accepted an accessor-rejected table.")
            } catch let error as NetLiftABIError {
                try require(error.status == 4, "Malformed accessor table returned an unexpected status.")
            }
            try require(netlift_fake_accessor_calls() == 1 &&
                        netlift_fake_registration_calls() == 0,
                        "Malformed accessor table reached registration or used fallback.")
        }

        for (flag, expectedStatus) in [
            (NETLIFT_FAKE_ACCESSOR_UNAVAILABLE, UInt32(11)),
            (NETLIFT_FAKE_NULL_TABLE_SUCCESS, UInt32(3)),
            (NETLIFT_FAKE_BAD_TABLE_VERSION | NETLIFT_FAKE_BYPASS_ACCESSOR_VALIDATION, UInt32(4)),
            (NETLIFT_FAKE_MISSING_CANCEL_REQUEST | NETLIFT_FAKE_BYPASS_ACCESSOR_VALIDATION, UInt32(4))
        ] {
            netlift_fake_reset()
            netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                             LIFT_FEATURE_TYPED_NATIVE_REPLY |
                                             LIFT_FEATURE_CALLBACK_LIFECYCLE_V2))
            netlift_fake_set_table_flags(UInt32(flag))
            let (v1, _) = try pointers()
            do {
                _ = try NetLiftNativeCallbackRegistrationV2_1(
                    v1Functions: v1,
                    handler: TestConfirmation { _ in true })
                throw SmokeFailure.assertion("Production initializer accepted an unavailable or invalid V2 table.")
            } catch let error as NetLiftABIError {
                try require(error.status == expectedStatus,
                            "Accessor failure or returned-table validation produced an unexpected status.")
            }
            try require(netlift_fake_accessor_calls() == 1 &&
                        netlift_fake_registration_calls() == 0,
                        "Accessor failure reached registration or selected a V1 fallback.")
        }
    }

    private static func acceptedReplyRetriesAndSerializesMainActorWork() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                         LIFT_FEATURE_TYPED_NATIVE_REPLY))
        netlift_fake_set_failures(1, 0)
        netlift_fake_set_table_flags(UInt32(NETLIFT_FAKE_STALE_GENERATION_CALLBACK))
        let (v1, v2) = try pointers()
        let counters = HandlerCounters()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { prompt in
                counters.enter()
                defer { counters.leave() }
                try await Task.sleep(nanoseconds: 2_000_000)
                return await MainActor.run { prompt == "hello" }
            })
        let result = try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        try require(result.id == 41 && result.sku == "SKU" && result.quantity == 3,
                    "Fake native result did not decode through generated public DTO codecs.")
        try require(counters.callCount == 2 && counters.maxActive == 1,
                    "V2 handler invocations were not serialized FIFO or a stale-generation callback was admitted.")
        registration.requestClose()
        try await registration.awaitClosed()
        try require(netlift_fake_reply_calls() >= 3,
                    "Non-OK typed reply was not retried with the original handler result.")
        try require(netlift_fake_ack_calls() == 1 && netlift_fake_release_calls() == 1 &&
                    netlift_fake_live_operations() == 0 && netlift_fake_live_requests() == 0,
                    "Successful close did not drain exactly once before release.")
    }

    private static func revokeRetiresActiveAndQueuedRequests() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                         LIFT_FEATURE_TYPED_NATIVE_REPLY))
        netlift_fake_set_failures(0, 1)
        let (v1, v2) = try pointers()
        let counters = HandlerCounters()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in
                counters.enter()
                defer { counters.leave() }
                try await Task.sleep(nanoseconds: 30_000_000_000)
                return true
            })
        let resultTask = Task {
            try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        }
        await Task.yield()
        let closeStart = DispatchTime.now().uptimeNanoseconds
        registration.requestClose()
        let closeElapsed = DispatchTime.now().uptimeNanoseconds - closeStart
        try require(closeElapsed < 100_000_000,
                    "requestClose blocked while callbacks were active.")
        try await registration.awaitClosed()
        do {
            _ = try await resultTask.value
            throw SmokeFailure.assertion("Revoked operation unexpectedly returned a result.")
        } catch is CancellationError { }
        try require(netlift_fake_cancel_request_calls() >= 3,
                    "Revoke did not retry and retire each active/queued managed request.")
        try require(netlift_fake_cancel_request_successes() == 2 &&
                    netlift_fake_reply_outcome_count(0) == 0,
                    "Cancellation did not win cleanly against unclaimed replies.")
        try require(counters.callCount <= 1,
                    "A queued request invoked the app handler after revoke began.")
        try require(netlift_fake_ack_calls() == 1 && netlift_fake_release_calls() == 1 &&
                    netlift_fake_live_operations() == 0 && netlift_fake_live_requests() == 0,
                    "Revoke acknowledged or released before managed cancellation completed.")
    }

    private static func replyAndRevokeClaimsAreExclusive() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                         LIFT_FEATURE_TYPED_NATIVE_REPLY))
        netlift_fake_set_table_flags(UInt32(NETLIFT_FAKE_REVOKE_DURING_REPLY))
        let (v1, v2) = try pointers()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in true })
        let resultTask = Task {
            try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        }
        do {
            _ = try await resultTask.value
            throw SmokeFailure.assertion("Revoke race unexpectedly returned an uncancelled operation.")
        } catch is CancellationError { }
        try await registration.awaitClosed()
        let claimedReplies = netlift_fake_reply_outcome_count(0)
        let confirmedCancellations = netlift_fake_cancel_request_successes()
        try require(claimedReplies == 1 && confirmedCancellations == 1 &&
                    claimedReplies + confirmedCancellations == 2,
                    "Reply and request cancellation did not produce exactly one disposition per request.")
        try require(netlift_fake_ack_calls() == 1 && netlift_fake_release_calls() == 1 &&
                    netlift_fake_live_operations() == 0 && netlift_fake_live_requests() == 0,
                    "Reply/revoke arbitration released before the native and managed work drained.")
    }

    private static func parentCancellationReturnsManagedRetirementReceipts() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_table_flags(UInt32(NETLIFT_FAKE_PARENT_CANCEL_RETIRES_REQUESTS))
        let (v1, v2) = try pointers()
        let counters = HandlerCounters()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in
                counters.enter()
                defer { counters.leave() }
                try await Task.sleep(nanoseconds: 30_000_000_000)
                return true
            })
        let operationTask = Task {
            try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        }
        for _ in 0..<1_000 where counters.callCount == 0 {
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        try require(counters.callCount > 0, "The parent-cancellation race handler did not start.")
        registration.requestClose()
        try await registration.awaitClosed()
        do {
            _ = try await operationTask.value
            throw SmokeFailure.assertion("Parent cancellation unexpectedly completed the operation.")
        } catch is CancellationError { }
        try require(netlift_fake_internal_cancel_calls() == 2 &&
                    netlift_fake_managed_cancel_calls() == 2 &&
                    netlift_fake_cancel_request_calls() == 2 &&
                    netlift_fake_duplicate_cancel_receipts() == 2 &&
                    netlift_fake_cancel_request_successes() == 2,
                    "The duplicate path did not receive the same managed-confirmed cancellation receipt.")
        try require(netlift_fake_live_requests() == 0 &&
                    netlift_fake_live_operations() == 0 &&
                    netlift_fake_ack_calls() == 1 &&
                    netlift_fake_release_calls() == 1,
                    "Drain acknowledged before duplicate cancellation receipts and operation completion.")
    }

    private static func cancelRequestRejectsUnknownStaleAndRepliedTokens() async throws
    {
        netlift_fake_reset()
        let (v1, v2) = try pointers()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in true })
        let result = try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        try require(result.id == 41, "The replied-token rejection setup did not complete.")
        let token = netlift_fake_registration_token()
        let generation = netlift_fake_registration_generation()
        let repliedRequest = netlift_fake_last_replied_request()
        try require(repliedRequest != 0, "The fake ABI did not retain a replied-request tombstone.")
        try require(netlift_fake_probe_cancel_request(token, generation, UInt64.max) == 10,
                    "Cancellation of an unknown request token was accepted.")
        try require(netlift_fake_probe_cancel_request(token, generation &+ 1, repliedRequest) == 10,
                    "Cancellation with a stale registration generation was accepted.")
        try require(netlift_fake_probe_cancel_request(token, generation, repliedRequest) == 10,
                    "A replied request was treated as a cancellation receipt.")
        try require(netlift_fake_managed_cancel_calls() == 0 &&
                    netlift_fake_duplicate_cancel_receipts() == 0,
                    "Invalid cancellation probes reached managed work or received a retirement receipt.")
        registration.requestClose()
        try await registration.awaitClosed()
    }

    private static func parentCancellationFailureRetriesThroughRequestCancellation() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_table_flags(UInt32(NETLIFT_FAKE_PARENT_CANCEL_RETIRES_REQUESTS))
        netlift_fake_set_failures(0, 1)
        let (v1, v2) = try pointers()
        let counters = HandlerCounters()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in
                counters.enter()
                defer { counters.leave() }
                try await Task.sleep(nanoseconds: 30_000_000_000)
                return true
            })
        let operationTask = Task {
            try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        }
        for _ in 0..<1_000 where counters.callCount == 0 {
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        try require(counters.callCount > 0, "The managed-cancellation retry handler did not start.")
        registration.requestClose()
        for _ in 0..<1_000 where netlift_fake_internal_cancel_calls() == 0 {
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        try require(netlift_fake_internal_cancel_calls() == 1 &&
                    netlift_fake_duplicate_cancel_receipts() == 0,
                    "A failed parent cancellation was treated as a retirement receipt.")
        do {
            try await registration.awaitClosed()
            throw SmokeFailure.assertion("A native parent-cancel failure was hidden from awaitClosed.")
        } catch let error as NetLiftABIError {
            try require(error.status == 11, "Parent cancellation failure had the wrong native status.")
        }
        do {
            _ = try await operationTask.value
            throw SmokeFailure.assertion("The cancelled operation unexpectedly returned a result.")
        } catch is CancellationError { }
        try await waitForRelease("Request-level retry did not recover the failed parent cancellation.")
        try require(netlift_fake_managed_cancel_calls() == 3 &&
                    netlift_fake_cancel_request_calls() == 2 &&
                    netlift_fake_cancel_request_successes() == 2 &&
                    netlift_fake_duplicate_cancel_receipts() == 0,
                    "Managed cancellation failure was not retried through the public request path.")
    }

    private static func permanentNativeFailuresAreObservableAndRecoverable() async throws
    {
        let (v1, v2) = try pointers()

        netlift_fake_reset()
        netlift_fake_set_permanent_failures(
            UInt32(NETLIFT_FAKE_PERMANENT_REPLY_FAILURE),
            8)
        let replyRegistration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in true })
        do {
            _ = try await replyRegistration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
            throw SmokeFailure.assertion("A permanently rejected typed reply completed its operation.")
        } catch let error as NetLiftCallbackLifecycleV2Error {
            try requireNativeCallFailure(error, "reply_typed", 8, "Reply failure was not observable.")
        }
        do {
            try await replyRegistration.awaitClosed()
            throw SmokeFailure.assertion("A failed typed reply was reported as a successful close.")
        } catch let error as NetLiftCallbackLifecycleV2Error {
            try requireNativeCallFailure(error, "reply_typed", 8, "Reply failure was not retained by awaitClosed.")
        }
        try require(netlift_fake_reply_calls() >= 3 && netlift_fake_reply_outcome_count(0) == 0,
                    "Permanent reply failure was not retried without consuming the request.")
        netlift_fake_set_permanent_failures(0, 11)
        try await waitForRelease("Reply failure did not recover after the fake host became available.")

        netlift_fake_reset()
        netlift_fake_set_permanent_failures(
            UInt32(NETLIFT_FAKE_PERMANENT_CANCEL_FAILURE),
            11)
        let counters = HandlerCounters()
        let suspended = SuspendedHandler()
        let cancelRegistration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in
                counters.enter()
                defer { counters.leave() }
                await suspended.suspend()
                return true
            })
        let operationTask = Task {
            try await cancelRegistration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        }
        for _ in 0..<1_000 where counters.callCount == 0 {
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        try require(counters.callCount > 0, "The cancellation-failure handler did not start.")
        cancelRegistration.requestClose()
        do {
            _ = try await operationTask.value
            throw SmokeFailure.assertion("A permanently rejected request cancellation completed its operation.")
        } catch let error as NetLiftCallbackLifecycleV2Error {
            try requireNativeCallFailure(error, "cancel_request", 11,
                                         "Cancellation failure was not observable to the operation.")
        }
        do {
            try await cancelRegistration.awaitClosed()
            throw SmokeFailure.assertion("A pending request was reported closed after cancellation failure.")
        } catch let error as NetLiftCallbackLifecycleV2Error {
            try requireNativeCallFailure(error, "cancel_request", 11,
                                         "Cancellation failure was not retained by awaitClosed.")
        }
        try require(netlift_fake_cancel_request_calls() >= 3 &&
                    netlift_fake_live_requests() > 0 &&
                    netlift_fake_live_operations() > 0 &&
                    netlift_fake_ack_calls() == 0 &&
                    netlift_fake_release_calls() == 0,
                    "Failed request cancellation consumed or released live native state.")
        netlift_fake_set_permanent_failures(0, 11)
        suspended.resume()
        try await waitForRelease("Cancellation failure did not recover after the fake host became available.")

        netlift_fake_reset()
        netlift_fake_set_permanent_failures(
            UInt32(NETLIFT_FAKE_PERMANENT_ACK_FAILURE),
            8)
        let ackRegistration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in true })
        ackRegistration.requestClose()
        do {
            try await ackRegistration.awaitClosed()
            throw SmokeFailure.assertion("A permanently rejected drain acknowledgement completed close.")
        } catch let error as NetLiftCallbackLifecycleV2Error {
            try requireNativeCallFailure(error, "ack_drain", 8,
                                         "Acknowledgement failure was not observable to awaitClosed.")
        }
        try require(netlift_fake_ack_calls() >= 3 && netlift_fake_release_calls() == 0,
                    "Failed acknowledgement was treated as release.")
        netlift_fake_set_permanent_failures(0, 11)
        try await waitForRelease("Drain acknowledgement did not recover after the fake host became available.")
    }

    private static func requireNativeCallFailure(
        _ error: NetLiftCallbackLifecycleV2Error,
        _ operation: String,
        _ status: UInt32,
        _ message: String) throws
    {
        guard case let .nativeCallFailed(failedOperation, failedStatus, attempts, _) = error,
              failedOperation == operation,
              failedStatus == status,
              attempts >= 3 else {
            throw SmokeFailure.assertion(message)
        }
    }

    private static func waitForRelease(_ message: String) async throws
    {
        for _ in 0..<2_000 where netlift_fake_release_calls() == 0 {
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        try require(netlift_fake_release_calls() == 1 &&
                    netlift_fake_live_requests() == 0 &&
                    netlift_fake_live_operations() == 0,
                    message)
    }

    private static func malformedAndOversizedRequestsAreDistinct() async throws
    {
        for mode in [UInt32(1), UInt32(2)] {
            netlift_fake_reset()
            netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                             LIFT_FEATURE_TYPED_NATIVE_REPLY))
            netlift_fake_set_payload_mode(mode)
            let (v1, v2) = try pointers()
            let counters = HandlerCounters()
            let registration = try NetLiftNativeCallbackRegistrationV2_1(
                testingV2Functions: v2,
                v1Functions: v1,
                handler: TestConfirmation { _ in
                    counters.enter()
                    defer { counters.leave() }
                    return true
                })
            _ = try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
            registration.requestClose()
            try await registration.awaitClosed()
            try require(counters.callCount == 0 &&
                        netlift_fake_reply_outcome_count(3) == 2,
                        "Invalid callback payload invoked app code or used the wrong reply outcome.")
            if mode == 1 {
                try require(netlift_fake_malformed_diagnostic_count() == 2 &&
                            netlift_fake_oversized_diagnostic_count() == 0,
                            "Malformed callback payload was not diagnosed distinctly.")
            } else {
                try require(netlift_fake_oversized_diagnostic_count() == 2 &&
                            netlift_fake_malformed_diagnostic_count() == 0,
                            "Oversized callback payload was not diagnosed distinctly.")
            }
        }
    }

    private static func reentrantCloseDoesNotDeadlock() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                         LIFT_FEATURE_TYPED_NATIVE_REPLY))
        let (v1, v2) = try pointers()
        let box = RegistrationBox()
        let counters = HandlerCounters()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in
                counters.enter()
                defer { counters.leave() }
                guard let registration = box.get() else { return false }
                registration.requestClose()
                do {
                    try await registration.awaitClosed()
                    return false
                } catch NetLiftCallbackLifecycleV2Error.reentrantAwaitClosed {
                    counters.markReentrantAwaitRejected()
                    return true
                }
            })
        box.set(registration)
        let resultTask = Task {
            try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        }
        do {
            _ = try await resultTask.value
        } catch is CancellationError { }
        try await registration.awaitClosed()
        try require(counters.rejectedReentrantAwait,
                    "A handler awaiting its own drain did not fail fast.")
        try require(counters.callCount == 1 && netlift_fake_cancel_request_calls() >= 2,
                    "Reentrant close invoked a queued handler or failed to retire both requests.")
    }

    private static func abandonedHandlerFailsClosedWithoutBlockingWaiters() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                         LIFT_FEATURE_TYPED_NATIVE_REPLY))
        let (v1, v2) = try pointers()
        let counters = HandlerCounters()
        let suspended = SuspendedHandler()
        let registration = try NetLiftNativeCallbackRegistrationV2_1(
            testingV2Functions: v2,
            v1Functions: v1,
            handler: TestConfirmation { _ in
                counters.enter()
                defer { counters.leave() }
                await suspended.suspend()
                return true
            })
        let resultTask = Task {
            try await registration.placeAsync(OrderDraft(sku: "SKU", quantity: 3))
        }
        for _ in 0..<1_000 where !suspended.isSuspended {
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        try require(suspended.isSuspended, "The uncooperative test handler did not enter its suspension.")
        let closeStart = DispatchTime.now().uptimeNanoseconds
        registration.requestClose()
        try require(DispatchTime.now().uptimeNanoseconds - closeStart < 100_000_000,
                    "requestClose waited for an uncooperative handler.")
        do {
            try await registration.awaitClosed()
            throw SmokeFailure.assertion("An unfinished local handler was reported as a successful close.")
        } catch NetLiftCallbackLifecycleV2Error.abandonedDrain { }
        try require(netlift_fake_cancel_request_calls() >= 2 &&
                    netlift_fake_live_requests() == 0 &&
                    netlift_fake_live_operations() == 0,
                    "Close failed to retire managed requests while a local handler was suspended.")
        suspended.resume()
        do {
            _ = try await resultTask.value
            throw SmokeFailure.assertion("The abandoned operation unexpectedly completed successfully.")
        } catch is CancellationError { }
        for _ in 0..<1_000 where netlift_fake_release_calls() == 0 {
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        try require(counters.callCount == 1 &&
                    netlift_fake_ack_calls() == 1 &&
                    netlift_fake_release_calls() == 1,
                    "A queued handler ran after revoke or cleanup failed to release after local work ended.")
    }

    private static func stressTenThousandCloseCycles() async throws
    {
        netlift_fake_reset()
        netlift_fake_set_features(UInt32(LIFT_FEATURE_ASYNC_CALLBACKS |
                                         LIFT_FEATURE_TYPED_NATIVE_REPLY))
        let (v1, v2) = try pointers()
        for _ in 0..<10_000 {
            let registration = try NetLiftNativeCallbackRegistrationV2_1(
                testingV2Functions: v2,
                v1Functions: v1,
                handler: TestConfirmation { _ in true })
            let result = try await registration.placeAsync(
                OrderDraft(sku: "SKU", quantity: 3))
            guard result.id == 41 else {
                throw SmokeFailure.assertion("Stress iteration returned an invalid DTO.")
            }
            registration.requestClose()
            try await registration.awaitClosed()
        }
        try require(netlift_fake_registration_calls() == 10_000 &&
                    netlift_fake_ack_calls() == 10_000 &&
                    netlift_fake_release_calls() == 10_000 &&
                    netlift_fake_live_operations() == 0 &&
                    netlift_fake_live_requests() == 0,
                    "10,000-cycle stress left live fake ABI work or mismatched release counts.")
    }
}
