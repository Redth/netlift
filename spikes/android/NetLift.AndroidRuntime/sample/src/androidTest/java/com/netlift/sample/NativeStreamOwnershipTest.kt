package com.netlift.sample

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeFailureHooks
import com.netlift.androidruntime.NetLiftRetentionMetrics
import com.netlift.androidruntime.NetLiftRetentionMemorySample
import com.netlift.androidruntime.NetLiftStreamCallbacks
import java.lang.ref.WeakReference
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

@RunWith(AndroidJUnit4::class)
class NativeStreamOwnershipTest {
    @Test
    fun warmedThreeBatchOrdersStreamRetentionTrendUsesExactOwnershipAndWeakProbes() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val expectedPid = android.os.Process.myPid()
        val checkpoints = mutableListOf<List<NetLiftRetentionMemorySample>>()

        repeat(CHECKPOINT_COUNT) { checkpoint ->
            val weakProbes = mutableListOf<WeakReference<Any>>()
            runOrdersStreamBatch(weakProbes)
            assertEquals("Orders stream workload changed process at checkpoint $checkpoint.",
                expectedPid, android.os.Process.myPid())
            assertEquals(0, collectKotlinWeakProbes(weakProbes))

            val samples = List(NetLiftRetentionMetrics.SamplesPerCheckpoint) {
                NetLiftRetentionMetrics.sample()
            }
            samples.forEachIndexed { sampleIndex, sample ->
                assertQuiescentManagedSnapshot(sample.managedSnapshot)
                assertEquals(0L, sample.managedSnapshot[WEAK_PROBE_REGISTRATIONS] +
                    sample.managedSnapshot[WEAK_PROBE_OPERATIONS] +
                    sample.managedSnapshot[WEAK_PROBE_REQUESTS] +
                    sample.managedSnapshot[WEAK_PROBE_STREAMS])
                assertNativeOwnershipQuiescent(
                    sample.nativeSnapshot,
                    "checkpoint=$checkpoint sample=$sampleIndex")
            }
            checkpoints.add(samples)
            android.util.Log.i(
                TAG,
                "stream retention checkpoint=$checkpoint cycles=$STREAMS_PER_BATCH pid=$expectedPid"
            )
            android.util.Log.i(
                TAG,
                "stream ownership checkpoint=$checkpoint kotlinWeakProbes=0 " +
                    "managed=${samples.last().managedSnapshot.contentToString()} " +
                    "native=${samples.last().nativeSnapshot.contentToString()}"
            )
            android.util.Log.i(
                TAG,
                NetLiftRetentionMetrics.formatCheckpoint("checkpoint=$checkpoint", samples)
            )
        }

        assertTrendLimits(checkpoints)
    }

    @Test
    fun hostedOrdersStreamReleaseReturnsNativeOwnershipToBaseline() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val baseline = ownershipSnapshot()
        val callbacks = StreamCallbacks()

        val token = subscribeOrdersStream(callbacks)
        val active = ownershipSnapshot()
        assertEquals(baseline[STREAM_CONTEXTS_LIVE] + 1, active[STREAM_CONTEXTS_LIVE])
        assertEquals(baseline[STREAM_REFS_LIVE] + 1, active[STREAM_REFS_LIVE])
        assertEquals(baseline[REGISTERED_STREAMS] + 1, active[REGISTERED_STREAMS])

        NetLiftHost.unsubscribeStream(token)
        assertTrue(callbacks.terminal.await(5, TimeUnit.SECONDS))
        assertTrue(callbacks.released.await(5, TimeUnit.SECONDS))
        assertEquals(1, callbacks.terminalCount.get())
        assertEquals(1, callbacks.releaseCount.get())
        assertOwnershipEventually { current ->
            current[STREAM_CONTEXTS_LIVE] == baseline[STREAM_CONTEXTS_LIVE] &&
                current[STREAM_REFS_LIVE] == baseline[STREAM_REFS_LIVE] &&
                current[REGISTERED_STREAMS] == baseline[REGISTERED_STREAMS] &&
                current[PENDING_STREAM_RELEASES] == baseline[PENDING_STREAM_RELEASES]
        }
    }

    @Test
    fun deferredOrdersStreamReleaseRemainsCountedUntilExplicitDrain() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val baseline = ownershipSnapshot()
        val callbacks = StreamCallbacks()
        val token = subscribeOrdersStream(callbacks)

        NetLiftNativeFailureHooks.failNextReleaseEnvironment()
        NetLiftHost.unsubscribeStream(token)

        assertFalse(callbacks.released.await(100, TimeUnit.MILLISECONDS))
        val queued = ownershipSnapshot()
        assertEquals(baseline[STREAM_CONTEXTS_LIVE] + 1, queued[STREAM_CONTEXTS_LIVE])
        assertEquals(baseline[STREAM_REFS_LIVE] + 1, queued[STREAM_REFS_LIVE])
        assertEquals(baseline[REGISTERED_STREAMS], queued[REGISTERED_STREAMS])
        assertEquals(baseline[PENDING_STREAM_RELEASES] + 1, queued[PENDING_STREAM_RELEASES])

        assertEquals(0, NetLiftNativeFailureHooks.drainPendingReleases())
        assertTrue(callbacks.terminal.await(5, TimeUnit.SECONDS))
        assertTrue(callbacks.released.await(5, TimeUnit.SECONDS))
        assertEquals(1, callbacks.terminalCount.get())
        assertEquals(1, callbacks.releaseCount.get())
        assertOwnershipEventually { current ->
            current[STREAM_CONTEXTS_LIVE] == baseline[STREAM_CONTEXTS_LIVE] &&
                current[STREAM_REFS_LIVE] == baseline[STREAM_REFS_LIVE] &&
                current[REGISTERED_STREAMS] == baseline[REGISTERED_STREAMS] &&
                current[PENDING_STREAM_RELEASES] == baseline[PENDING_STREAM_RELEASES]
        }
        assertQuiescentManagedSnapshot(managedRetentionSnapshot())
    }

    @Test
    fun releasedOrdersStreamSubscriptionAndCallbacksReturnToManagedBaseline() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        assertQuiescentManagedSnapshot(managedRetentionSnapshot())

        val callbacks = subscribeAndReleaseOrdersStream()
        val after = managedRetentionSnapshot()
        assertQuiescentManagedSnapshot(after)
        assertEquals(0L, after[WEAK_PROBE_STREAMS])
        assertEquals(0, collectKotlinWeakProbe(callbacks))
    }

    private fun subscribeOrdersStream(callbacks: NetLiftStreamCallbacks): Long =
        NetLiftHost.subscribeStream(
            serviceId = 2,
            subscriptionId = 2,
            arguments = byteArrayOf(),
            callbacks = callbacks,
            initialCredit = 0,
            transportCapacity = 64,
            maxItemBytes = 4096
        )

    private fun ownershipSnapshot(): LongArray {
        val snapshot = requireNotNull(NetLiftNativeFailureHooks.ownershipSnapshot())
        assertEquals(SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals(SNAPSHOT_SCHEMA_VERSION.toLong(), snapshot[SNAPSHOT_VERSION])
        return snapshot
    }

    private fun managedRetentionSnapshot(): LongArray {
        val snapshot = requireNotNull(NetLiftNativeFailureHooks.managedRetentionSnapshot())
        assertEquals(MANAGED_SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals(MANAGED_SNAPSHOT_SCHEMA_VERSION.toLong(),
            snapshot[MANAGED_SNAPSHOT_VERSION])
        return snapshot
    }

    private fun assertQuiescentManagedSnapshot(snapshot: LongArray) {
        assertEquals(MANAGED_SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals(MANAGED_SNAPSHOT_SCHEMA_VERSION.toLong(),
            snapshot[MANAGED_SNAPSHOT_VERSION])
        for (index in MANAGED_QUIESCENT_FIELDS)
            assertEquals("Managed retention field $index was not quiescent.",
                0L, snapshot[index])
        assertEquals(snapshot[REGISTRATION_SLOTS], snapshot[REGISTRATION_FREE_SLOTS])
        assertEquals(snapshot[OPERATION_SLOTS], snapshot[OPERATION_FREE_SLOTS])
        assertEquals(snapshot[REQUEST_SLOTS], snapshot[REQUEST_FREE_SLOTS])
        assertEquals(snapshot[STREAM_SLOTS], snapshot[STREAM_FREE_SLOTS])
    }

    private fun subscribeAndReleaseOrdersStream(): WeakReference<Any> {
        val callbacks = StreamCallbacks()
        val token = subscribeOrdersStream(callbacks)
        NetLiftHost.unsubscribeStream(token)
        assertTrue(callbacks.terminal.await(5, TimeUnit.SECONDS))
        assertTrue(callbacks.released.await(5, TimeUnit.SECONDS))
        assertEquals(1, callbacks.terminalCount.get())
        assertEquals(1, callbacks.releaseCount.get())
        assertOwnershipEventually { current ->
            current[STREAM_CONTEXTS_LIVE] == 0L &&
                current[STREAM_REFS_LIVE] == 0L &&
                current[REGISTERED_STREAMS] == 0L &&
                current[PENDING_STREAM_RELEASES] == 0L
        }
        return WeakReference<Any>(callbacks)
    }

    private fun collectKotlinWeakProbe(probe: WeakReference<Any>): Int {
        repeat(MAX_GC_ATTEMPTS) {
            System.gc()
            System.runFinalization()
            if (probe.get() == null)
                return 0
        }
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(GC_WAIT_SECONDS)
        while (System.nanoTime() < deadline) {
            if (probe.get() == null)
                return 0
            Thread.sleep(GC_RETRY_DELAY_MILLIS)
        }
        return if (probe.get() == null) 0 else 1
    }

    private fun runOrdersStreamBatch(weakProbes: MutableList<WeakReference<Any>>) {
        repeat(STREAMS_PER_BATCH) {
            weakProbes.add(subscribeUnsubscribeOrdersStream())
        }
    }

    private fun subscribeUnsubscribeOrdersStream(): WeakReference<Any> {
        val callbacks = StreamCallbacks()
        val token = subscribeOrdersStream(callbacks)
        NetLiftHost.unsubscribeStream(token)
        check(callbacks.terminal.await(STREAM_RELEASE_TIMEOUT_SECONDS, TimeUnit.SECONDS)) {
            "Orders stream did not deliver terminal during retention workload."
        }
        check(callbacks.released.await(STREAM_RELEASE_TIMEOUT_SECONDS, TimeUnit.SECONDS)) {
            "Orders stream did not release during retention workload."
        }
        check(callbacks.terminalCount.get() == 1 && callbacks.releaseCount.get() == 1) {
            "Orders stream terminal/release was not exactly once."
        }
        return WeakReference<Any>(callbacks)
    }

    private fun collectKotlinWeakProbes(probes: MutableList<WeakReference<Any>>): Int {
        try {
            repeat(MAX_GC_ATTEMPTS) {
                System.gc()
                System.runFinalization()
                if (probes.none { it.get() != null })
                    return 0
            }
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(GC_WAIT_SECONDS)
            while (System.nanoTime() < deadline) {
                if (probes.none { it.get() != null })
                    return 0
                Thread.sleep(GC_RETRY_DELAY_MILLIS)
            }
            return probes.count { it.get() != null }
        } finally {
            probes.clear()
        }
    }

    private fun assertNativeOwnershipQuiescent(snapshot: LongArray, label: String) {
        assertEquals(SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals(SNAPSHOT_SCHEMA_VERSION.toLong(), snapshot[SNAPSHOT_VERSION])
        for (index in NATIVE_QUIESCENT_FIELDS)
            assertEquals("$label native ownership field $index was not quiescent.",
                0L, snapshot[index])
        assertEquals("$label callback context create/release totals differ.",
            snapshot[CALLBACK_CONTEXTS_CREATED], snapshot[CALLBACK_CONTEXTS_DESTROYED])
        assertEquals("$label callback global-ref create/release totals differ.",
            snapshot[CALLBACK_REFS_CREATED], snapshot[CALLBACK_REFS_DESTROYED])
        assertEquals("$label stream context create/release totals differ.",
            snapshot[STREAM_CONTEXTS_CREATED], snapshot[STREAM_CONTEXTS_DESTROYED])
        assertEquals("$label stream global-ref create/release totals differ.",
            snapshot[STREAM_REFS_CREATED], snapshot[STREAM_REFS_DESTROYED])
    }

    private fun assertTrendLimits(checkpoints: List<List<NetLiftRetentionMemorySample>>) {
        val managed = NetLiftRetentionMetrics.assess(
            name = "managed-live-bytes",
            checkpoints = checkpoints,
            value = { it.managedSnapshot[MANAGED_LIVE_BYTES] },
            growthLimit = NetLiftRetentionMetrics.ManagedGrowthLimitBytes,
            slopeLimit = NetLiftRetentionMetrics.ManagedSlopeLimitBytes
        )
        val native = NetLiftRetentionMetrics.assess(
            name = "android-native-heap-allocated-bytes",
            checkpoints = checkpoints,
            value = { it.nativeHeapAllocatedBytes },
            growthLimit = NetLiftRetentionMetrics.NativeGrowthLimitBytes,
            slopeLimit = NetLiftRetentionMetrics.NativeSlopeLimitBytes
        )
        listOf(managed, native).forEach { assessment ->
            android.util.Log.i(TAG, "stream retention metric=$assessment")
            assertTrue(
                "${assessment.name} exceeded a predeclared retention limit: $assessment",
                assessment.status == "PASS" ||
                    assessment.status == "INCONCLUSIVE_BASELINE_NOISE"
            )
        }

        val baselinePssKb = NetLiftRetentionMetrics.median(
            checkpoints.first().map { it.totalPssKb })
        val maxMeasuredPssKb = checkpoints.drop(1).maxOf { samples ->
            NetLiftRetentionMetrics.median(samples.map { it.totalPssKb })
        }
        if (maxMeasuredPssKb - baselinePssKb >
            NetLiftRetentionMetrics.PssInvestigationAlertKb
        ) {
            android.util.Log.w(TAG, "PSS investigation alert only: baselineMedian=" +
                "${baselinePssKb}KB, maxMeasuredMedian=${maxMeasuredPssKb}KB.")
        }
    }

    private fun assertOwnershipEventually(assertion: (LongArray) -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        do {
            if (assertion(ownershipSnapshot())) return
            Thread.sleep(10)
        } while (System.nanoTime() < deadline)
        assertTrue(
            "Native stream ownership counters did not return to baseline",
            assertion(ownershipSnapshot())
        )
    }

    private class StreamCallbacks : NetLiftStreamCallbacks {
        val terminal = CountDownLatch(1)
        val released = CountDownLatch(1)
        val terminalCount = AtomicInteger()
        val releaseCount = AtomicInteger()

        override fun onNext(token: Long, borrowedItem: ByteArray) = Unit

        override fun onTerminal(token: Long, status: Int, diagnostic: ByteArray) {
            terminalCount.incrementAndGet()
            terminal.countDown()
        }

        override fun onRelease(token: Long) {
            releaseCount.incrementAndGet()
            released.countDown()
        }
    }

    private companion object {
        const val TAG = "NetLiftStreamRetention"
        const val SNAPSHOT_SCHEMA_VERSION = 1
        const val SNAPSHOT_VERSION = 0
        const val STREAM_CONTEXTS_LIVE = 11
        const val STREAM_CONTEXTS_CREATED = 9
        const val STREAM_CONTEXTS_DESTROYED = 10
        const val STREAM_REFS_LIVE = 15
        const val STREAM_REFS_CREATED = 13
        const val STREAM_REFS_DESTROYED = 14
        const val REGISTERED_STREAMS = 18
        const val PENDING_STREAM_RELEASES = 20
        const val SNAPSHOT_FIELD_COUNT = 22
        const val MANAGED_SNAPSHOT_SCHEMA_VERSION = 1
        const val MANAGED_SNAPSHOT_VERSION = 0
        const val STREAM_SLOTS = 18
        const val STREAM_FREE_SLOTS = 19
        const val REGISTRATION_SLOTS = 2
        const val REGISTRATION_FREE_SLOTS = 3
        const val OPERATION_SLOTS = 5
        const val OPERATION_FREE_SLOTS = 6
        const val REQUEST_SLOTS = 8
        const val REQUEST_FREE_SLOTS = 9
        const val WEAK_PROBE_REGISTRATIONS = 23
        const val WEAK_PROBE_OPERATIONS = 24
        const val WEAK_PROBE_REQUESTS = 25
        const val WEAK_PROBE_STREAMS = 26
        const val MANAGED_SNAPSHOT_FIELD_COUNT = 34
        const val MANAGED_LIVE_BYTES = 27
        const val STREAMS_PER_BATCH = 10_850
        const val CHECKPOINT_COUNT = 4
        const val STREAM_RELEASE_TIMEOUT_SECONDS = 30L
        const val MAX_GC_ATTEMPTS = 3
        const val GC_RETRY_DELAY_MILLIS = 100L
        const val GC_WAIT_SECONDS = 5L
        const val CALLBACK_CONTEXTS_CREATED = 1
        const val CALLBACK_CONTEXTS_DESTROYED = 2
        const val CALLBACK_REFS_CREATED = 5
        const val CALLBACK_REFS_DESTROYED = 6
        val NATIVE_QUIESCENT_FIELDS = intArrayOf(
            3, 7, STREAM_CONTEXTS_LIVE, STREAM_REFS_LIVE,
            17, REGISTERED_STREAMS, 19, PENDING_STREAM_RELEASES, 21
        )
        val MANAGED_QUIESCENT_FIELDS = intArrayOf(
            1, 4, 7, 10, 11, 12, 13, 14, 15, 16, 17, 20, 21, 22
        )
    }
}
