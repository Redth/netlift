package com.netlift.secondcontract

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidhost.contract.DispatchRequest
import com.netlift.androidhost.contract.NetLiftFlatDtoWire
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.NetLiftNativeFailureHooks
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class NativeApiCallLeaseTest {
    @Test
    fun releaseWaitsForCancelCleanupAfterChildCancellationAndResult() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val contract = NetLiftHost.initialize(context)
        val baseline = ownershipSnapshot()
        val callbacks = LeaseCallbacks()
        val registration =
            NetLiftHost.registerCallbacksV2(contract.confirmationServiceId, callbacks)
        val cancelFailure = AtomicReference<Exception?>()
        val cancelReturned = CountDownLatch(1)
        var worker: Thread? = null
        var barrierArmed = false
        var revoked = false
        try {
            val operationToken = NetLiftHost.begin(
                contract.ordersServiceId,
                contract.placeOrderMethodId,
                registration.token,
                NetLiftFlatDtoWire.encodeDispatchRequest(
                    DispatchRequest("jni-cancel-lease", 1L)
                )
            )
            NetLiftHost.activate(operationToken)
            assertTrue(callbacks.requestReceived.await(TEST_TIMEOUT_SECONDS, TimeUnit.SECONDS))

            val cancelWorker = Thread({
                try {
                    NetLiftHost.cancel(operationToken)
                } catch (failure: Exception) {
                    cancelFailure.set(failure)
                } finally {
                    cancelReturned.countDown()
                }
            }, "netlift-native-cancel-cleanup-test")
            worker = cancelWorker
            NetLiftNativeFailureHooks.armCallCleanupBarrier(CANCEL_CALL)
            barrierArmed = true
            cancelWorker.start()
            assertTrue(
                "nativeCancel did not reach post-managed cleanup.",
                NetLiftNativeFailureHooks.awaitCallCleanupBarrier(
                    CANCEL_CALL,
                    TEST_TIMEOUT_MILLIS
                )
            )
            assertTrue(
                "The managed terminal result was not delivered before JNI cleanup.",
                callbacks.resultReceived.await(TEST_TIMEOUT_SECONDS, TimeUnit.SECONDS)
            )

            NetLiftHost.revokeV2(registration.token)
            revoked = true
            assertReleaseDeferred(baseline, callbacks)
        } finally {
            if (barrierArmed)
                NetLiftNativeFailureHooks.releaseCallCleanupBarrier()
            worker?.join(TEST_TIMEOUT_MILLIS)
            if (!revoked && callbacks.releaseCount.get() == 0) {
                try {
                    NetLiftHost.revokeV2(registration.token)
                } catch (_: Exception) {
                    // Preserve the primary test failure while closing the registration.
                }
            }
        }

        val completedWorker = checkNotNull(worker)
        assertFalse("The nativeCancel caller did not finish.", completedWorker.isAlive)
        assertTrue("The nativeCancel caller did not return.", cancelReturned.await(0, TimeUnit.MILLISECONDS))
        assertEquals(null, cancelFailure.get())
        assertEquals(1, callbacks.resultCount.get())
        assertReleaseDelivered(baseline, callbacks)
    }

    @Test
    fun reentrantAckDoesNotReleaseInsideRevokeJniCleanup() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val contract = NetLiftHost.initialize(context)
        val baseline = ownershipSnapshot()
        val callbacks = LeaseCallbacks()
        val registration =
            NetLiftHost.registerCallbacksV2(contract.confirmationServiceId, callbacks)
        val revokeFailure = AtomicReference<Exception?>()
        val revokeReturned = CountDownLatch(1)
        val worker = Thread({
            try {
                NetLiftHost.revokeV2(registration.token)
            } catch (failure: Exception) {
                revokeFailure.set(failure)
            } finally {
                revokeReturned.countDown()
            }
        }, "netlift-native-revoke-cleanup-test")

        var barrierArmed = false
        try {
            NetLiftNativeFailureHooks.armCallCleanupBarrier(REVOKE_V2_CALL)
            barrierArmed = true
            worker.start()
            assertTrue(
                "nativeRevokeV2 did not reach post-managed cleanup.",
                NetLiftNativeFailureHooks.awaitCallCleanupBarrier(
                    REVOKE_V2_CALL,
                    TEST_TIMEOUT_MILLIS
                )
            )
            assertEquals(1, callbacks.ackCount.get())
            assertReleaseDeferred(baseline, callbacks)
        } finally {
            if (barrierArmed)
                NetLiftNativeFailureHooks.releaseCallCleanupBarrier()
            worker.join(TEST_TIMEOUT_MILLIS)
            if (callbacks.releaseCount.get() == 0) {
                try {
                    NetLiftHost.revokeV2(registration.token)
                } catch (_: Exception) {
                    // Preserve the primary test failure while closing the registration.
                }
            }
        }

        assertFalse("The nativeRevokeV2 caller did not finish.", worker.isAlive)
        assertTrue("The nativeRevokeV2 caller did not return.", revokeReturned.await(0, TimeUnit.MILLISECONDS))
        assertEquals(null, revokeFailure.get())
        assertReleaseDelivered(baseline, callbacks)
    }

    @Test
    fun releaseWaitsForAckDrainJniCleanup() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val contract = NetLiftHost.initialize(context)
        val baseline = ownershipSnapshot()
        val callbacks = LeaseCallbacks(ackOnRevoke = false)
        val registration =
            NetLiftHost.registerCallbacksV2(contract.confirmationServiceId, callbacks)
        NetLiftHost.revokeV2(registration.token)
        assertEquals(0, callbacks.releaseCount.get())

        val ackFailure = AtomicReference<Exception?>()
        val ackReturned = CountDownLatch(1)
        val worker = Thread({
            try {
                NetLiftHost.ackDrain(registration.token, registration.generation)
            } catch (failure: Exception) {
                ackFailure.set(failure)
            } finally {
                ackReturned.countDown()
            }
        }, "netlift-native-ack-cleanup-test")

        var barrierArmed = false
        try {
            NetLiftNativeFailureHooks.armCallCleanupBarrier(ACK_DRAIN_CALL)
            barrierArmed = true
            worker.start()
            assertTrue(
                "nativeAckDrainV2 did not reach post-managed cleanup.",
                NetLiftNativeFailureHooks.awaitCallCleanupBarrier(
                    ACK_DRAIN_CALL,
                    TEST_TIMEOUT_MILLIS
                )
            )
            assertReleaseDeferred(baseline, callbacks)
        } finally {
            if (barrierArmed)
                NetLiftNativeFailureHooks.releaseCallCleanupBarrier()
            worker.join(TEST_TIMEOUT_MILLIS)
            if (callbacks.releaseCount.get() == 0) {
                try {
                    NetLiftHost.ackDrain(registration.token, registration.generation)
                } catch (_: Exception) {
                    // Preserve the primary test failure while closing the registration.
                }
            }
        }

        assertFalse("The nativeAckDrainV2 caller did not finish.", worker.isAlive)
        assertTrue("The nativeAckDrainV2 caller did not return.", ackReturned.await(0, TimeUnit.MILLISECONDS))
        assertEquals(null, ackFailure.get())
        assertReleaseDelivered(baseline, callbacks)
    }

    private fun assertReleaseDeferred(baseline: LongArray, callbacks: LeaseCallbacks) {
        assertOwnershipEventually { current ->
            current[CALLBACK_CONTEXTS_LIVE] == baseline[CALLBACK_CONTEXTS_LIVE] + 1 &&
                current[CALLBACK_REFS_LIVE] == baseline[CALLBACK_REFS_LIVE] + 1 &&
                current[REGISTERED_CALLBACKS] == baseline[REGISTERED_CALLBACKS] &&
                current[PENDING_CALLBACK_RELEASES] == baseline[PENDING_CALLBACK_RELEASES] + 1
        }
        assertFalse(
            "OnRelease overtook the native table-call cleanup lease.",
            callbacks.released.await(100, TimeUnit.MILLISECONDS)
        )
        assertEquals(0, callbacks.releaseCount.get())
    }

    private fun assertReleaseDelivered(baseline: LongArray, callbacks: LeaseCallbacks) {
        assertTrue(
            "Deferred OnRelease was not delivered after native cleanup.",
            callbacks.released.await(TEST_TIMEOUT_SECONDS, TimeUnit.SECONDS)
        )
        assertEquals(1, callbacks.releaseCount.get())
        assertOwnershipEventually { current ->
            current[CALLBACK_CONTEXTS_LIVE] == baseline[CALLBACK_CONTEXTS_LIVE] &&
                current[CALLBACK_REFS_LIVE] == baseline[CALLBACK_REFS_LIVE] &&
                current[REGISTERED_CALLBACKS] == baseline[REGISTERED_CALLBACKS] &&
                current[PENDING_CALLBACK_RELEASES] == baseline[PENDING_CALLBACK_RELEASES]
        }
    }

    private fun ownershipSnapshot(): LongArray {
        val snapshot = requireNotNull(NetLiftNativeFailureHooks.ownershipSnapshot())
        assertEquals(SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals(SNAPSHOT_SCHEMA_VERSION.toLong(), snapshot[SNAPSHOT_VERSION])
        return snapshot
    }

    private fun assertOwnershipEventually(assertion: (LongArray) -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(TEST_TIMEOUT_SECONDS)
        do {
            if (assertion(ownershipSnapshot())) return
            Thread.sleep(10)
        } while (System.nanoTime() < deadline)
        assertTrue("Native ownership did not reach the expected state.", assertion(ownershipSnapshot()))
    }

    private class LeaseCallbacks(
        private val ackOnRevoke: Boolean = true
    ) : NetLiftNativeCallbacksV2 {
        val released = CountDownLatch(1)
        val requestReceived = CountDownLatch(1)
        val resultReceived = CountDownLatch(1)
        val releaseCount = AtomicInteger()
        val resultCount = AtomicInteger()
        val ackCount = AtomicInteger()

        override fun onRequest(
            registrationToken: Long,
            requestToken: Long,
            methodId: Int,
            payload: ByteArray
        ) {
            requestReceived.countDown()
        }

        override fun onResult(
            operationToken: Long,
            status: Int,
            result: ByteArray,
            error: ByteArray
        ) {
            resultCount.incrementAndGet()
            resultReceived.countDown()
        }

        override fun onRelease(registrationToken: Long) {
            releaseCount.incrementAndGet()
            released.countDown()
        }

        override fun onRevoke(registrationToken: Long, generation: Long) {
            if (ackOnRevoke) {
                ackCount.incrementAndGet()
                NetLiftHost.ackDrain(registrationToken, generation)
            }
        }
    }

    private companion object {
        const val SNAPSHOT_SCHEMA_VERSION = 1
        const val SNAPSHOT_VERSION = 0
        const val CALLBACK_CONTEXTS_LIVE = 3
        const val CALLBACK_REFS_LIVE = 7
        const val REGISTERED_CALLBACKS = 17
        const val PENDING_CALLBACK_RELEASES = 19
        const val SNAPSHOT_FIELD_COUNT = 22
        const val CANCEL_CALL = 1
        const val REVOKE_V2_CALL = 2
        const val ACK_DRAIN_CALL = 3
        const val TEST_TIMEOUT_SECONDS = 5L
        const val TEST_TIMEOUT_MILLIS = 5_000L
    }
}
