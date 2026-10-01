import Foundation

private final class FakeLiftStreamABI: LiftStreamABI, @unchecked Sendable
{
    private let lock = NSLock()
    private var storedCallbacks: LiftStreamCallbacks?
    private var storedEvents: [String] = []
    private var requests = 0
    private var unsubscribeCalls = 0
    private let failActivation: Bool
    private let wrongHash: Bool
    private let expectedSubscriptionID: UInt32
    private let itemPayloads: [Data]
    var onSubscribe: (@Sendable () -> Void)?

    init(
        failActivation: Bool = false,
        wrongHash: Bool = false,
        expectedSubscriptionID: UInt32 = 4,
        itemPayloads: [Data] = [Data([7]), Data([8])])
    {
        self.failActivation = failActivation
        self.wrongHash = wrongHash
        self.expectedSubscriptionID = expectedSubscriptionID
        self.itemPayloads = itemPayloads
    }

    private func record(_ value: String)
    {
        lock.lock()
        storedEvents.append(value)
        lock.unlock()
    }

    var events: [String]
    {
        lock.lock()
        defer { lock.unlock() }
        return storedEvents
    }

    func start(abiVersion: UInt32, schemaHash: [UInt8]) throws -> LiftStreamRuntimeInfo
    {
        record("start")
        var actualHash = schemaHash
        if wrongHash { actualHash[0] ^= 1 }
        return LiftStreamRuntimeInfo(abiVersion: abiVersion, featureBits: 8, schemaHash: actualHash)
    }

    func subscribe(
        serviceID: UInt32,
        subscriptionID: UInt32,
        callbacks: LiftStreamCallbacks,
        initialCredit: UInt32,
        transportCapacity: UInt32,
        maxItemBytes: UInt32) throws -> UInt64
    {
        guard serviceID == 20, subscriptionID == expectedSubscriptionID, initialCredit == 0,
              transportCapacity == 64, maxItemBytes == 4096 else
        {
            throw LiftStreamFailure.protocolError("Invalid stream subscription arguments.")
        }
        lock.lock()
        storedCallbacks = callbacks
        storedEvents.append("subscribe")
        lock.unlock()
        onSubscribe?()
        return 0x0000000100000007
    }

    func activate(_ token: UInt64) throws
    {
        record("activate")
        if failActivation { throw LiftStreamFailure.protocolError("activation failed") }
    }

    func request(_ token: UInt64, credits: UInt32) throws
    {
        guard credits == 1 else { throw LiftStreamFailure.protocolError("credit window exceeded") }
        lock.lock()
        requests += 1
        let request = requests
        let callbacks = storedCallbacks
        storedEvents.append("request-\(request)")
        lock.unlock()
        if request <= itemPayloads.count
        {
            callbacks?.onNext(token, itemPayloads[request - 1])
        } else {
            callbacks?.onTerminal(token, 0, Data())
        }
    }

    func unsubscribe(_ token: UInt64)
    {
        record("unsubscribe")
        lock.lock()
        unsubscribeCalls += 1
        let callbacks = storedCallbacks
        lock.unlock()
        callbacks?.onRelease(token)
        record("release")
    }

    var unsubscribeCount: Int
    {
        lock.lock()
        defer { lock.unlock() }
        return unsubscribeCalls
    }
}

private func encodedField(_ value: Int32) -> Data
{
    let bits = UInt32(bitPattern: value)
    return Data([
        1, 0, 4, 0, 0, 0,
        UInt8(truncatingIfNeeded: bits),
        UInt8(truncatingIfNeeded: bits >> 8),
        UInt8(truncatingIfNeeded: bits >> 16),
        UInt8(truncatingIfNeeded: bits >> 24)
    ])
}

private func encodedNullField() -> Data
{
    Data([1, 0, 255, 255, 255, 255])
}

private func encodedStringField(_ value: String) -> Data
{
    let bytes = Array(value.utf8)
    let length = UInt32(bytes.count)
    return Data([1, 0,
        UInt8(truncatingIfNeeded: length),
        UInt8(truncatingIfNeeded: length >> 8),
        UInt8(truncatingIfNeeded: length >> 16),
        UInt8(truncatingIfNeeded: length >> 24)] + bytes)
}

private final class SwiftTaskHolder: @unchecked Sendable
{
    private let lock = NSLock()
    private var task: Task<Void, Error>?
    func set(_ task: Task<Void, Error>)
    {
        lock.lock()
        self.task = task
        lock.unlock()
    }
    func cancel()
    {
        lock.lock()
        let task = self.task
        lock.unlock()
        task?.cancel()
    }
}

@main
private struct StreamSwiftSmoke
{
    static func main() async throws
    {
        let abi = FakeLiftStreamABI()
        let sequence = liftStream_20_4(using: abi) { data in
            guard let value = data.first else { throw LiftStreamFailure.protocolError("Empty test item.") }
            return Pulse(value: Int32(value))
        }
        var iterator = sequence.makeAsyncIterator()
        var values: [Int32] = []
        while let pulse = try await iterator.next() { values.append(pulse.value) }
        await iterator.close()
        guard values == [7, 8] else { throw LiftStreamFailure.protocolError("Stream items were not ordered.") }
        guard abi.events.filter({ $0.hasPrefix("request-") }).count == 3 else
        {
            throw LiftStreamFailure.protocolError("Swift facade did not request one credit per consumed item.")
        }
        guard abi.events.firstIndex(of: "subscribe")! < abi.events.firstIndex(of: "activate")! else
        {
            throw LiftStreamFailure.protocolError("Stream activation preceded token publication.")
        }
        print("PASS Swift cold stream handshake and one-credit-per-next demand")

        let failed = FakeLiftStreamABI(failActivation: true)
        var failedIterator = liftStream_20_4(using: failed) { _ in Pulse(value: 0) }.makeAsyncIterator()
        do
        {
            _ = try await failedIterator.next()
            throw LiftStreamFailure.protocolError("Failed Activate produced an item.")
        }
        catch let failure as LiftStreamFailure
        {
            guard case .protocolError("activation failed") = failure else { throw failure }
        }
        await failedIterator.close()
        guard failed.events.contains("unsubscribe") && failed.events.contains("release") &&
              !failed.events.contains(where: { $0.hasPrefix("request-") }) else
        {
            throw LiftStreamFailure.protocolError("Failed Activate did not clean up its provisional subscription.")
        }

        let mismatched = FakeLiftStreamABI(wrongHash: true)
        var mismatchIterator = liftStream_20_4(using: mismatched) { _ in Pulse(value: 0) }.makeAsyncIterator()
        do
        {
            _ = try await mismatchIterator.next()
            throw LiftStreamFailure.protocolError("Stream accepted a mismatched schema hash.")
        }
        catch let failure as LiftStreamFailure
        {
            guard case .protocolError("Stream ABI version, feature, or schema hash mismatch.") = failure else
            {
                throw failure
            }
        }
        print("PASS Swift exact hash rejection and failed-activation cleanup")

        let holder = SwiftTaskHolder()
        let cancelledBeforeActivate = FakeLiftStreamABI()
        cancelledBeforeActivate.onSubscribe = { holder.cancel() }
        let collection = Task<Void, Error> {
            await Task.yield()
            var cancelledIterator = liftStream_20_4(using: cancelledBeforeActivate) { _ in Pulse(value: 0) }
                .makeAsyncIterator()
            _ = try await cancelledIterator.next()
        }
        holder.set(collection)
        do
        {
            try await collection.value
            throw LiftStreamFailure.protocolError("Cancellation before Activate was ignored.")
        }
        catch is CancellationError { }
        guard cancelledBeforeActivate.events.contains("unsubscribe") &&
              !cancelledBeforeActivate.events.contains("activate") &&
              !cancelledBeforeActivate.events.contains(where: { $0.hasPrefix("request-") }) else
        {
            throw LiftStreamFailure.protocolError("Pre-activation cancellation did not clean the provisional token.")
        }
        print("PASS Swift cancel-before-activation cleanup")

        let dtoABI = FakeLiftStreamABI(itemPayloads: [encodedField(7), encodedField(8)])
        var dtoIterator = liftStream_20_4(using: dtoABI).makeAsyncIterator()
        var dtoValues: [Int32] = []
        while let pulse = try await dtoIterator.next() { dtoValues.append(pulse.value) }
        await dtoIterator.close()
        guard dtoValues == [7, 8] else
        {
            throw LiftStreamFailure.protocolError("Generated Pulse item codec did not round-trip.")
        }
        print("PASS generated Swift flat-DTO item codec without a caller decoder")

        let scalarABI = FakeLiftStreamABI(
            expectedSubscriptionID: 6,
            itemPayloads: [encodedField(17)])
        var scalarIterator = liftStream_20_6(using: scalarABI).makeAsyncIterator()
        var scalarValues: [Int32] = []
        while let value = try await scalarIterator.next() { scalarValues.append(value) }
        await scalarIterator.close()
        guard scalarValues == [17] else
        {
            throw LiftStreamFailure.protocolError("Generated scalar item codec did not round-trip.")
        }
        print("PASS generated Swift scalar item codec without a caller decoder")

        let stringABI = FakeLiftStreamABI(
            expectedSubscriptionID: 17,
            itemPayloads: [encodedStringField("Café")])
        var stringIterator = liftStream_20_17(using: stringABI).makeAsyncIterator()
        var stringValues: [String] = []
        while let value = try await stringIterator.next() { stringValues.append(value) }
        await stringIterator.close()
        guard stringValues == ["Café"] else
        {
            throw LiftStreamFailure.protocolError("Generated UTF-8 scalar item codec did not round-trip.")
        }
        print("PASS generated Swift UTF-8 scalar item codec")

        let nullableABI = FakeLiftStreamABI(
            expectedSubscriptionID: 18,
            itemPayloads: [encodedNullField()])
        var nullableIterator = liftStream_20_18(using: nullableABI).makeAsyncIterator()
        var nullableValues: [String?] = []
        while let value = try await nullableIterator.next() { nullableValues.append(value) }
        await nullableIterator.close()
        guard nullableValues.count == 1, nullableValues[0] == nil else
        {
            throw LiftStreamFailure.protocolError("Generated nullable scalar sentinel did not round-trip.")
        }
        print("PASS generated Swift nullable scalar null sentinel")

        let malformedItems = [
            Data([2, 0, 4, 0, 0, 0, 7, 0, 0, 0]),
            Data([1, 0, 4, 0, 0, 0, 7]),
            Data([1, 0, 4, 0, 0, 0, 7, 0, 0, 0, 99])
        ]
        for malformedItem in malformedItems
        {
            let malformedABI = FakeLiftStreamABI(itemPayloads: [malformedItem])
            var malformedIterator = liftStream_20_4(using: malformedABI).makeAsyncIterator()
            var decodeFailed = false
            do { _ = try await malformedIterator.next() }
            catch { decodeFailed = true }
            await malformedIterator.close()
            guard decodeFailed, malformedABI.unsubscribeCount == 1,
                  malformedABI.events.filter({ $0 == "release" }).count == 1 else
            {
                throw LiftStreamFailure.protocolError("Malformed Swift item was dropped or failed to drain exactly once.")
            }
        }
        let invalidUtf8ABI = FakeLiftStreamABI(
            expectedSubscriptionID: 17,
            itemPayloads: [Data([1, 0, 1, 0, 0, 0, 255])])
        var invalidUtf8Iterator = liftStream_20_17(using: invalidUtf8ABI).makeAsyncIterator()
        var invalidUtf8Failed = false
        do { _ = try await invalidUtf8Iterator.next() }
        catch { invalidUtf8Failed = true }
        await invalidUtf8Iterator.close()
        guard invalidUtf8Failed, invalidUtf8ABI.unsubscribeCount == 1 else
        {
            throw LiftStreamFailure.protocolError("Invalid UTF-8 scalar item was accepted or did not drain.")
        }
        print("PASS Swift unknown/truncated/trailing item payloads throw and unsubscribe exactly once")
    }
}
