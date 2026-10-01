package com.netlift.secondcontract

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.NetLiftNativeFailureHooks
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

@RunWith(AndroidJUnit4::class)
class NativeOwnershipFailureTest {
    @Test
    fun registrationRollbackAndDeferredReleaseAreFailClosed() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val baseline = ownershipSnapshot()

        val rollbackCallbacks = LifecycleCallbacks()
        val handoffFailure = assertThrows(IllegalStateException::class.java) {
            NetLiftNativeFailureHooks.failNextV2Handoff(
                serviceId = 42,
                callbacks = rollbackCallbacks,
                failAbort = false
            )
        }
        assertTrue(handoffFailure.message.orEmpty().contains("Injected V2 token-array handoff failure"))
        assertOwnershipEventually { current ->
            current[CALLBACK_CONTEXTS_LIVE] == baseline[CALLBACK_CONTEXTS_LIVE] &&
                current[CALLBACK_REFS_LIVE] == baseline[CALLBACK_REFS_LIVE] &&
                current[REGISTERED_CALLBACKS] == baseline[REGISTERED_CALLBACKS] &&
                current[QUARANTINED_CALLBACKS] == baseline[QUARANTINED_CALLBACKS]
        }

        val followUpCallbacks = LifecycleCallbacks()
        val followUp = NetLiftHost.registerCallbacksV2(42, followUpCallbacks)
        val active = ownershipSnapshot()
        assertEquals(baseline[CALLBACK_CONTEXTS_LIVE] + 1, active[CALLBACK_CONTEXTS_LIVE])
        assertEquals(baseline[CALLBACK_REFS_LIVE] + 1, active[CALLBACK_REFS_LIVE])
        assertEquals(baseline[REGISTERED_CALLBACKS] + 1, active[REGISTERED_CALLBACKS])
        NetLiftHost.revokeV2(followUp.token)
        assertTrue(followUpCallbacks.released.await(5, TimeUnit.SECONDS))
        assertEquals(1, followUpCallbacks.releaseCount.get())
        assertOwnershipEventually { current ->
            current[CALLBACK_CONTEXTS_LIVE] == baseline[CALLBACK_CONTEXTS_LIVE] &&
                current[CALLBACK_REFS_LIVE] == baseline[CALLBACK_REFS_LIVE] &&
                current[REGISTERED_CALLBACKS] == baseline[REGISTERED_CALLBACKS] &&
                current[PENDING_CALLBACK_RELEASES] == baseline[PENDING_CALLBACK_RELEASES]
        }

        val deferredCallbacks = LifecycleCallbacks()
        val deferred = NetLiftHost.registerCallbacksV2(42, deferredCallbacks)
        NetLiftNativeFailureHooks.failNextReleaseEnvironment()
        NetLiftHost.revokeV2(deferred.token)
        assertTrue(deferredCallbacks.released.await(5, TimeUnit.SECONDS))
        assertEquals(1, deferredCallbacks.releaseCount.get())
        assertOwnershipEventually { current ->
            current[CALLBACK_CONTEXTS_LIVE] == baseline[CALLBACK_CONTEXTS_LIVE] &&
                current[CALLBACK_REFS_LIVE] == baseline[CALLBACK_REFS_LIVE] &&
                current[REGISTERED_CALLBACKS] == baseline[REGISTERED_CALLBACKS] &&
                current[PENDING_CALLBACK_RELEASES] == baseline[PENDING_CALLBACK_RELEASES]
        }

        val quarantinedCallbacks = LifecycleCallbacks()
        val rollbackFailure = assertThrows(IllegalStateException::class.java) {
            NetLiftNativeFailureHooks.failNextV2Handoff(
                serviceId = 42,
                callbacks = quarantinedCallbacks,
                failAbort = true
            )
        }
        assertTrue(rollbackFailure.message.orEmpty().contains("Injected V2 token-array handoff failure"))
        val poisonedRegistration = assertThrows(IllegalStateException::class.java) {
            NetLiftHost.registerCallbacksV2(42, LifecycleCallbacks())
        }
        assertTrue(
            poisonedRegistration.message.orEmpty()
                .contains("disabled after an unconfirmed rollback")
        )
        val quarantined = ownershipSnapshot()
        assertEquals(baseline[CALLBACK_CONTEXTS_LIVE] + 1, quarantined[CALLBACK_CONTEXTS_LIVE])
        assertEquals(baseline[CALLBACK_REFS_LIVE] + 1, quarantined[CALLBACK_REFS_LIVE])
        assertEquals(baseline[REGISTERED_CALLBACKS] + 1, quarantined[REGISTERED_CALLBACKS])
        assertEquals(baseline[QUARANTINED_CALLBACKS] + 1, quarantined[QUARANTINED_CALLBACKS])
    }

    private fun ownershipSnapshot(): LongArray {
        val snapshot = requireNotNull(NetLiftNativeFailureHooks.ownershipSnapshot())
        assertEquals(SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals(SNAPSHOT_SCHEMA_VERSION.toLong(), snapshot[SNAPSHOT_VERSION])
        return snapshot
    }

    private fun assertOwnershipEventually(assertion: (LongArray) -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        do {
            if (assertion(ownershipSnapshot())) return
            Thread.sleep(10)
        } while (System.nanoTime() < deadline)
        assertTrue(
            "Native ownership counters did not reach the expected state",
            assertion(ownershipSnapshot())
        )
    }

    private companion object {
        const val SNAPSHOT_SCHEMA_VERSION = 1
        const val SNAPSHOT_VERSION = 0
        const val CALLBACK_CONTEXTS_LIVE = 3
        const val CALLBACK_REFS_LIVE = 7
        const val REGISTERED_CALLBACKS = 17
        const val PENDING_CALLBACK_RELEASES = 19
        const val QUARANTINED_CALLBACKS = 21
        const val SNAPSHOT_FIELD_COUNT = 22
    }

    private class LifecycleCallbacks : NetLiftNativeCallbacksV2 {
        val released = CountDownLatch(1)
        val releaseCount = AtomicInteger()

        override fun onRequest(
            registrationToken: Long,
            requestToken: Long,
            methodId: Int,
            payload: ByteArray
        ) = Unit

        override fun onResult(
            operationToken: Long,
            status: Int,
            result: ByteArray,
            error: ByteArray
        ) = Unit

        override fun onRelease(registrationToken: Long) {
            releaseCount.incrementAndGet()
            released.countDown()
        }

        override fun onRevoke(registrationToken: Long, generation: Long) {
            NetLiftHost.ackDrain(registrationToken, generation)
        }
    }
}
