package com.netlift.secondcontract

import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeFailureHooks
import com.netlift.androidruntime.NetLiftRetentionMetrics
import com.netlift.androidruntime.NetLiftRetentionMemorySample
import kotlinx.coroutines.runBlocking
import java.lang.ref.WeakReference
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class RealJniOwnershipStressTest {
    @Test
    fun warmedThreeBatchCallbackRetentionTrendUsesExactOwnershipAndWeakProbes() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val expectedPid = android.os.Process.myPid()
        val checkpoints = mutableListOf<List<NetLiftRetentionMemorySample>>()

        repeat(CHECKPOINT_COUNT) { checkpoint ->
            val weakProbes = mutableListOf<WeakReference<Any>>()
            val summary = SecondContractJniStressRunner.run(context, weakProbes) { }
            assertEquals("Callback workload changed process during checkpoint $checkpoint.",
                expectedPid, summary.pid)
            assertEquals(CYCLES_PER_BATCH, summary.cycles)
            assertEquals(summary.cycles, summary.releases)
            assertEquals(0, collectKotlinWeakProbes(weakProbes))

            val samples = List(NetLiftRetentionMetrics.SamplesPerCheckpoint) {
                NetLiftRetentionMetrics.sample()
            }
            samples.forEachIndexed { sampleIndex, sample ->
                assertQuiescentManagedSnapshot(
                    sample.managedSnapshot,
                    "checkpoint=$checkpoint sample=$sampleIndex")
                assertEquals(0L, sample.managedSnapshot[WEAK_PROBE_REGISTRATIONS] +
                    sample.managedSnapshot[WEAK_PROBE_OPERATIONS] +
                    sample.managedSnapshot[WEAK_PROBE_REQUESTS] +
                    sample.managedSnapshot[WEAK_PROBE_STREAMS])
                assertNativeOwnershipQuiescent(
                    sample.nativeSnapshot,
                    "checkpoint=$checkpoint sample=$sampleIndex")
            }
            checkpoints.add(samples)
            Log.i(TAG, "retention checkpoint=$checkpoint pid=$expectedPid summary=$summary")
            Log.i(TAG, "ownership checkpoint=$checkpoint kotlinWeakProbes=0 " +
                "managed=${samples.last().managedSnapshot.contentToString()} " +
                "native=${samples.last().nativeSnapshot.contentToString()}")
            Log.i(TAG, NetLiftRetentionMetrics.formatCheckpoint("checkpoint=$checkpoint", samples))
        }

        assertTrendLimits(checkpoints)
    }

    @Test
    fun tenThousandPlusRealJniCyclesReturnCallbackOwnershipToBaseline() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val before = ownershipSnapshot()
        val beforeManaged = managedRetentionSnapshot()
        val weakProbes = mutableListOf<WeakReference<Any>>()
        val summary = SecondContractJniStressRunner.run(context, weakProbes) { }
        val afterManaged = managedRetentionSnapshot()
        val after = ownershipSnapshot()
        val liveKotlinProbes = collectKotlinWeakProbes(weakProbes)

        assertTrue("Expected at least 10,000 JNI cycles", summary.cycles >= 10_000)
        assertEquals(summary.cycles, summary.releases)
        assertEquals(before[CALLBACK_CONTEXTS_LIVE], after[CALLBACK_CONTEXTS_LIVE])
        assertEquals(before[CALLBACK_REFS_LIVE], after[CALLBACK_REFS_LIVE])
        assertEquals(before[REGISTERED_CALLBACKS], after[REGISTERED_CALLBACKS])
        assertEquals(before[PENDING_CALLBACK_RELEASES], after[PENDING_CALLBACK_RELEASES])
        assertEquals(before[QUARANTINED_CALLBACKS], after[QUARANTINED_CALLBACKS])
        assertEquals(after[CALLBACK_CONTEXTS_CREATED] - before[CALLBACK_CONTEXTS_CREATED],
            after[CALLBACK_CONTEXTS_DESTROYED] - before[CALLBACK_CONTEXTS_DESTROYED])
        assertEquals(after[CALLBACK_REFS_CREATED] - before[CALLBACK_REFS_CREATED],
            after[CALLBACK_REFS_DESTROYED] - before[CALLBACK_REFS_DESTROYED])
        assertQuiescentManagedSnapshot(beforeManaged, "baseline")
        assertQuiescentManagedSnapshot(afterManaged, "after stress")
        assertEquals("Managed weak probes survived full collections.", 0L,
            afterManaged[WEAK_PROBE_REGISTRATIONS] +
                afterManaged[WEAK_PROBE_OPERATIONS] +
                afterManaged[WEAK_PROBE_REQUESTS] +
                afterManaged[WEAK_PROBE_STREAMS])
        assertEquals("Kotlin callback/client weak probes survived System.gc().", 0,
            liveKotlinProbes)

        val report =
            "summary=$summary; before=${before.contentToString()}; " +
                "after=${after.contentToString()}; managedBefore=${beforeManaged.contentToString()}; " +
                "managedAfter=${afterManaged.contentToString()}; liveKotlinProbes=$liveKotlinProbes"
        Log.i(TAG, report)
        println("$TAG: $report")
    }

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

    private fun assertQuiescentManagedSnapshot(snapshot: LongArray, label: String) {
        assertEquals("$label managed snapshot field count.",
            MANAGED_SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals("$label managed snapshot schema.",
            MANAGED_SNAPSHOT_SCHEMA_VERSION.toLong(), snapshot[MANAGED_SNAPSHOT_VERSION])
        for (index in QUIESCENT_FIELDS)
            assertEquals("$label managed retention field $index was not quiescent.",
                0L, snapshot[index])
        assertEquals("$label registration table free slots differ from capacity.",
            snapshot[REGISTRATION_SLOTS], snapshot[REGISTRATION_FREE_SLOTS])
        assertEquals("$label operation table free slots differ from capacity.",
            snapshot[OPERATION_SLOTS], snapshot[OPERATION_FREE_SLOTS])
        assertEquals("$label request table free slots differ from capacity.",
            snapshot[REQUEST_SLOTS], snapshot[REQUEST_FREE_SLOTS])
        assertEquals("$label stream table free slots differ from capacity.",
            snapshot[STREAM_SLOTS], snapshot[STREAM_FREE_SLOTS])
    }

    private fun assertNativeOwnershipQuiescent(snapshot: LongArray, label: String) {
        assertEquals("$label native snapshot field count.", SNAPSHOT_FIELD_COUNT, snapshot.size)
        assertEquals("$label native snapshot schema.",
            SNAPSHOT_SCHEMA_VERSION.toLong(), snapshot[SNAPSHOT_VERSION])
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
            Log.i(TAG, "retention metric=$assessment")
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
            Log.w(TAG, "PSS investigation alert only: baselineMedian=${baselinePssKb}KB, " +
                "maxMeasuredMedian=${maxMeasuredPssKb}KB.")
        }
    }

    private fun collectKotlinWeakProbes(probes: MutableList<WeakReference<Any>>): Int {
        try {
            repeat(MAX_GC_ATTEMPTS) {
                System.gc()
                System.runFinalization()
                if (probes.none { it.get() != null })
                    return 0
            }
            val deadline = System.nanoTime() +
                java.util.concurrent.TimeUnit.SECONDS.toNanos(GC_WAIT_SECONDS)
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

    private companion object {
        const val TAG = "NetLiftOwnershipStress"
        const val SNAPSHOT_SCHEMA_VERSION = 1
        const val SNAPSHOT_VERSION = 0
        const val CALLBACK_CONTEXTS_CREATED = 1
        const val CALLBACK_CONTEXTS_DESTROYED = 2
        const val CALLBACK_CONTEXTS_LIVE = 3
        const val STREAM_CONTEXTS_CREATED = 9
        const val STREAM_CONTEXTS_DESTROYED = 10
        const val STREAM_CONTEXTS_LIVE = 11
        const val CALLBACK_REFS_CREATED = 5
        const val CALLBACK_REFS_DESTROYED = 6
        const val CALLBACK_REFS_LIVE = 7
        const val STREAM_REFS_CREATED = 13
        const val STREAM_REFS_DESTROYED = 14
        const val STREAM_REFS_LIVE = 15
        const val REGISTERED_CALLBACKS = 17
        const val REGISTERED_STREAMS = 18
        const val PENDING_CALLBACK_RELEASES = 19
        const val PENDING_STREAM_RELEASES = 20
        const val QUARANTINED_CALLBACKS = 21
        const val SNAPSHOT_FIELD_COUNT = 22
        const val MANAGED_LIVE_BYTES = 27
        const val CYCLES_PER_BATCH = 10_850
        const val CHECKPOINT_COUNT = 4
        const val MANAGED_SNAPSHOT_SCHEMA_VERSION = 1
        const val MANAGED_SNAPSHOT_VERSION = 0
        const val REGISTRATIONS_OCCUPIED = 1
        const val REGISTRATION_SLOTS = 2
        const val REGISTRATION_FREE_SLOTS = 3
        const val OPERATIONS_OCCUPIED = 4
        const val OPERATION_SLOTS = 5
        const val OPERATION_FREE_SLOTS = 6
        const val REQUESTS_OCCUPIED = 7
        const val REQUEST_SLOTS = 8
        const val REQUEST_FREE_SLOTS = 9
        const val CHILD_PENDING_OPERATIONS = 10
        const val CHILD_ACTIVE_OPERATIONS = 11
        const val CHILD_REQUESTS = 12
        const val ACTIVE_CALLBACKS = 13
        const val REVOKING_REGISTRATIONS = 14
        const val RELEASE_STARTED_REGISTRATIONS = 15
        const val ACKNOWLEDGED_REGISTRATIONS = 16
        const val STREAMS_OCCUPIED = 17
        const val STREAM_SLOTS = 18
        const val STREAM_FREE_SLOTS = 19
        const val STREAM_ACTIVE_CALLBACKS = 20
        const val STREAM_PRODUCERS = 21
        const val STREAM_CANCELLATIONS = 22
        const val WEAK_PROBE_REGISTRATIONS = 23
        const val WEAK_PROBE_OPERATIONS = 24
        const val WEAK_PROBE_REQUESTS = 25
        const val WEAK_PROBE_STREAMS = 26
        const val MANAGED_SNAPSHOT_FIELD_COUNT = 34
        const val MAX_GC_ATTEMPTS = 3
        const val GC_RETRY_DELAY_MILLIS = 100L
        const val GC_WAIT_SECONDS = 5L
        val QUIESCENT_FIELDS = intArrayOf(
            REGISTRATIONS_OCCUPIED,
            OPERATIONS_OCCUPIED,
            REQUESTS_OCCUPIED,
            CHILD_PENDING_OPERATIONS,
            CHILD_ACTIVE_OPERATIONS,
            CHILD_REQUESTS,
            ACTIVE_CALLBACKS,
            REVOKING_REGISTRATIONS,
            RELEASE_STARTED_REGISTRATIONS,
            ACKNOWLEDGED_REGISTRATIONS,
            STREAMS_OCCUPIED,
            STREAM_ACTIVE_CALLBACKS,
            STREAM_PRODUCERS,
            STREAM_CANCELLATIONS
        )
        val NATIVE_QUIESCENT_FIELDS = intArrayOf(
            CALLBACK_CONTEXTS_LIVE,
            CALLBACK_REFS_LIVE,
            STREAM_CONTEXTS_LIVE,
            STREAM_REFS_LIVE,
            REGISTERED_CALLBACKS,
            REGISTERED_STREAMS,
            PENDING_CALLBACK_RELEASES,
            PENDING_STREAM_RELEASES,
            QUARANTINED_CALLBACKS
        )
    }
}
