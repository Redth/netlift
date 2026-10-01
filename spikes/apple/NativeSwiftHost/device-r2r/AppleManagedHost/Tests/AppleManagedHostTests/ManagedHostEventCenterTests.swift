import XCTest
@testable import AppleManagedHost

final class ManagedHostEventCenterTests: XCTestCase {
    func testEventsAreBroadcastAndRetainTypedPayloads() async {
        let center = ManagedHostEventCenter()
        let first = center.events()
        let second = center.events()
        let confirmation = ManagedHostEvent.confirmationRequested(
            ManagedHostConfirmation(requestID: 17, title: "Confirm item"))
        let result = ManagedHostEvent.operationCompleted(
            ManagedHostOperationResult(
                operationID: 9,
                status: 0,
                result: Data([1, 2, 3]),
                error: Data()))

        XCTAssertTrue(center.publish(confirmation))
        XCTAssertTrue(center.publish(result))

        var firstIterator = first.makeAsyncIterator()
        var secondIterator = second.makeAsyncIterator()
        let firstConfirmation = await firstIterator.next()
        let firstResult = await firstIterator.next()
        let secondConfirmation = await secondIterator.next()
        let secondResult = await secondIterator.next()
        XCTAssertEqual(firstConfirmation, confirmation)
        XCTAssertEqual(firstResult, result)
        XCTAssertEqual(secondConfirmation, confirmation)
        XCTAssertEqual(secondResult, result)
    }

    func testCancellationEndsOnlyTheCancelledSubscriber() async {
        let center = ManagedHostEventCenter()
        let cancelled = center.events()
        let active = center.events()
        let cancelledTask = Task {
            for await _ in cancelled {}
        }
        cancelledTask.cancel()
        await cancelledTask.value

        let event = ManagedHostEvent.confirmationRequested(
            ManagedHostConfirmation(requestID: 1, title: "Next"))
        center.publish(event)

        var iterator = active.makeAsyncIterator()
        let activeEvent = await iterator.next()
        XCTAssertEqual(activeEvent, event)
    }

    func testFullSubscriberBufferRejectsTheNewEvent() async {
        let center = ManagedHostEventCenter()
        let stream = center.events()

        for requestID in 1...64 {
            XCTAssertTrue(center.publish(
                .confirmationRequested(
                    ManagedHostConfirmation(requestID: Int64(requestID), title: "Queued"))))
        }
        XCTAssertFalse(center.publish(
            .confirmationRequested(
                ManagedHostConfirmation(requestID: 65, title: "Rejected"))))

        var iterator = stream.makeAsyncIterator()
        let first = await iterator.next()
        XCTAssertEqual(
            first,
            .confirmationRequested(
                ManagedHostConfirmation(requestID: 1, title: "Queued")))
    }

    func testPublishReportsPartialDeliveryWhenASubscriberIsFull() async {
        let center = ManagedHostEventCenter()
        let acceptingStream = center.events()
        let fullStream = center.events()
        var acceptingIterator = acceptingStream.makeAsyncIterator()

        for requestID in 1...64 {
            let event = ManagedHostEvent.confirmationRequested(
                ManagedHostConfirmation(requestID: Int64(requestID), title: "Queued"))
            XCTAssertTrue(center.publish(event))
            let acceptedEvent = await acceptingIterator.next()
            XCTAssertEqual(acceptedEvent, event)
        }

        let partiallyDelivered = ManagedHostEvent.confirmationRequested(
            ManagedHostConfirmation(requestID: 65, title: "Partial"))
        XCTAssertFalse(center.publish(partiallyDelivered))
        let acceptedEvent = await acceptingIterator.next()
        XCTAssertEqual(acceptedEvent, partiallyDelivered)

        var fullIterator = fullStream.makeAsyncIterator()
        let firstQueuedEvent = await fullIterator.next()
        XCTAssertEqual(
            firstQueuedEvent,
            .confirmationRequested(
                ManagedHostConfirmation(requestID: 1, title: "Queued")))
    }
}
