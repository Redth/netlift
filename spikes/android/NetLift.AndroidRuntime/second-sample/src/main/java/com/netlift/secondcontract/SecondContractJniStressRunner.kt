package com.netlift.secondcontract

import android.content.Context
import android.os.Debug
import android.util.Log
import com.netlift.androidhost.contract.DispatchRequest
import com.netlift.androidhost.contract.NetLiftFlatDtoWire
import com.netlift.androidruntime.NativeAbiException
import com.netlift.androidruntime.NetLiftHost
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.lang.ref.WeakReference
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

internal object SecondContractJniStressRunner {
    private const val TotalCycles = 10_850
    private const val IdleCycles = 100
    private const val AcceptedCycles = 9_250
    private const val PendingCycles = 750
    private const val ClaimedReplyCycles = 750
    private const val ProgressInterval = 250
    private const val DrainTimeoutMillis = 30_000L

    data class Summary(
        val cycles: Int,
        val registrations: Int,
        val generationReuseTransitions: Int,
        val idleCloses: Int,
        val acceptedDispatches: Int,
        val pendingRequestRevokes: Int,
        val claimedReplyRevokes: Int,
        val requestCallbacks: Int,
        val claimedReplies: Int,
        val replyCalls: Int,
        val revokeCallbacks: Int,
        val drainAcks: Int,
        val releases: Int,
        val staleAckRejections: Int,
        val duplicateAckRejections: Int,
        val duplicateRevokeRejections: Int,
        val injectedStaleCallbacks: Int,
        val injectedDuplicateCallbacks: Int,
        val replyErrorsAfterRevoke: Int,
        val reentrantIdleAcks: Int,
        val postReleaseBeginRejections: Int,
        val pid: Int,
        val pssBeforeKb: Long,
        val pssAfterKb: Long,
        val elapsedMillis: Long
    )

    suspend fun run(
        context: Context,
        weakProbes: MutableList<WeakReference<Any>>? = null,
        onProgress: suspend (String) -> Unit
    ): Summary = withContext(Dispatchers.IO) {
        val applicationContext = context.applicationContext
        val callbackScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val requestCallbacks = AtomicInteger()
        val claimedReplies = AtomicInteger()
        val replyCalls = AtomicInteger()
        val revokeCallbacks = AtomicInteger()
        val drainAcks = AtomicInteger()
        val releases = AtomicInteger()
        val staleAckRejections = AtomicInteger()
        val duplicateAckRejections = AtomicInteger()
        val duplicateRevokeRejections = AtomicInteger()
        val injectedStaleCallbacks = AtomicInteger()
        val injectedDuplicateCallbacks = AtomicInteger()
        val replyErrorsAfterRevoke = AtomicInteger()
        var idleCloses = 0
        var acceptedDispatches = 0
        var pendingRequestRevokes = 0
        var claimedReplyRevokes = 0
        var reentrantIdleAcks = 0
        var postReleaseBeginRejections = 0
        var previousSlot = -1L
        var previousGeneration = 0L
        var registrations = 0
        var generationReuseTransitions = 0
        var activeClient: SecondContractClient? = null
        var activeReplyGate: CountDownLatch? = null
        val startedAt = System.nanoTime()
        System.gc()
        val pssBeforeKb = Debug.getPss()
        val contract = NetLiftHost.initialize(applicationContext)

        try {
            withTimeout(20 * 60 * 1_000L) {
                repeat(TotalCycles) { cycle ->
                    val kind = when {
                        cycle < IdleCycles -> Mode.IDLE
                        cycle < TotalCycles - PendingCycles - ClaimedReplyCycles -> Mode.ACCEPT
                        cycle < TotalCycles - ClaimedReplyCycles -> Mode.PENDING
                        else -> Mode.CLAIMED_REPLY
                    }
                    val requestEntered = CompletableDeferred<Unit>()
                    val replyClaimed = CompletableDeferred<Unit>()
                    val allowReplyCall = CountDownLatch(1)
                    if (kind == Mode.CLAIMED_REPLY)
                        activeReplyGate = allowReplyCall
                    val probe = Probe(
                        requestCallbacks,
                        claimedReplies,
                        replyCalls,
                        revokeCallbacks,
                        drainAcks,
                        releases,
                        replyErrorsAfterRevoke,
                        replyClaimed,
                        allowReplyCall,
                        kind == Mode.CLAIMED_REPLY
                    )
                    val callbackErrors = mutableListOf<Exception>()
                    val client = SecondContractClient.create(
                        applicationContext,
                        callbackScope,
                        observer = probe,
                        onError = { failure -> synchronized(callbackErrors) { callbackErrors.add(failure) } }
                    ) {
                        requestEntered.complete(Unit)
                        when (kind) {
                            Mode.ACCEPT, Mode.CLAIMED_REPLY -> true
                            Mode.PENDING -> awaitCancellation()
                            Mode.IDLE -> error("Idle registration received an unexpected request.")
                        }
                    }
                    weakProbes?.add(WeakReference<Any>(client))
                    weakProbes?.add(WeakReference<Any>(probe))
                    activeClient = client
                    registrations++
                    val (token, generation) = client.diagnosticIdentity()
                    check(token != 0L && generation != 0L) { "Cycle $cycle has an empty V2 identity." }
                    val slot = token and 0xffff_ffffL
                    if (previousSlot >= 0) {
                        check(previousSlot == slot) {
                            "Released registration slot was lost or not reused at cycle $cycle."
                        }
                        check(generation == previousGeneration + 1) {
                            "Reused registration slot did not advance generation at cycle $cycle."
                        }
                        generationReuseTransitions++
                    }
                    previousSlot = slot
                    previousGeneration = generation

                    expectStale("stale-generation drain ack") {
                        NetLiftHost.ackDrain(token, generation + 1)
                    }
                    staleAckRejections.incrementAndGet()

                    if (cycle < 10) {
                        client.onRevoke(token, generation + 1)
                        injectedStaleCallbacks.incrementAndGet()
                    }

                    when (kind) {
                        Mode.IDLE -> {
                            idleCloses++
                            client.requestClose()
                            val observedErrors =
                                synchronized(callbackErrors) { callbackErrors.map { it.message } }
                            check(probe.ackCount.get() == 1 && probe.releaseCount.get() == 1) {
                                "Idle close returned with ack/release " +
                                    "${probe.ackCount.get()}/${probe.releaseCount.get()} and " +
                                    "errors=$observedErrors."
                            }
                            reentrantIdleAcks++
                        }
                        Mode.ACCEPT -> {
                            acceptedDispatches++
                            supervisorScope {
                                val dispatch = async(Dispatchers.IO) {
                                    client.dispatch(request(cycle))
                                }
                                val receipt = withTimeout(DrainTimeoutMillis) { dispatch.await() }
                                check(receipt.approved && receipt.sequence == cycle.toLong()) {
                                    "Accepted JNI dispatch returned an invalid receipt at cycle $cycle."
                                }
                            }
                            client.requestClose()
                        }
                        Mode.PENDING -> {
                            pendingRequestRevokes++
                            supervisorScope {
                                val dispatch = async(Dispatchers.IO) {
                                    client.dispatch(request(cycle))
                                }
                                withTimeout(DrainTimeoutMillis) { requestEntered.await() }
                                client.requestClose()
                                val result = runCatching {
                                    withTimeout(DrainTimeoutMillis) { dispatch.await() }
                                }
                                check(result.isFailure) {
                                    "Revoked pending approval unexpectedly completed at cycle $cycle."
                                }
                            }
                        }
                        Mode.CLAIMED_REPLY -> {
                            claimedReplyRevokes++
                            supervisorScope {
                                val dispatch = async(Dispatchers.IO) {
                                    client.dispatch(request(cycle))
                                }
                                withTimeout(DrainTimeoutMillis) { replyClaimed.await() }
                                client.requestClose()
                                check(releases.get() < cycle + 1) {
                                    "Native release occurred while a claimed reply was held at cycle $cycle."
                                }
                                allowReplyCall.countDown()
                                runCatching {
                                    withTimeout(DrainTimeoutMillis) { dispatch.await() }
                                }
                            }
                        }
                    }

                    withTimeout(DrainTimeoutMillis) { client.awaitClosed() }
                    check(client.diagnosticSnapshot() == SecondContractClient.DiagnosticSnapshot(
                        pendingOperations = 0,
                        callbackRequests = 0,
                        activeRequestCallbacks = 0,
                        activeReplyCalls = 0,
                        callbackJobs = 0,
                        released = true
                    )) {
                        "Cycle $cycle retained client state after awaitClosed: " +
                            client.diagnosticSnapshot()
                    }
                    check(probe.revokeCount.get() == 1) {
                        "Cycle $cycle observed ${probe.revokeCount.get()} native revoke callbacks."
                    }
                    check(probe.ackCount.get() == 1) {
                        "Cycle $cycle observed ${probe.ackCount.get()} native drain acknowledgements."
                    }
                    check(probe.releaseCount.get() == 1) {
                        "Cycle $cycle observed ${probe.releaseCount.get()} native releases."
                    }
                    check(
                        probe.eventOrder.indexOf("revoke") < probe.eventOrder.indexOf("ack") &&
                            probe.eventOrder.indexOf("ack") < probe.eventOrder.indexOf("release")
                    ) {
                        "Cycle $cycle violated revoke/ack/release callback ordering: ${probe.eventOrder}"
                    }
                    expectStale("duplicate revoke after release") { NetLiftHost.revokeV2(token) }
                    duplicateRevokeRejections.incrementAndGet()
                    if (cycle < 10) {
                        client.onRevoke(token, generation)
                        injectedDuplicateCallbacks.incrementAndGet()
                    }
                    val expectedErrors = (if (cycle < 10) 2 else 0) +
                        (if (kind == Mode.CLAIMED_REPLY && probe.lastReplyFailure.get()) 1 else 0)
                    check(synchronized(callbackErrors) { callbackErrors.size } == expectedErrors) {
                        "Cycle $cycle observed unexpected callback errors: " +
                            synchronized(callbackErrors) { callbackErrors.map { it.message } }
                    }
                    if (kind == Mode.CLAIMED_REPLY)
                        check(probe.eventOrder.indexOf("reply-return") < probe.eventOrder.indexOf("ack")) {
                            "Cycle $cycle acknowledged drain before the claimed native reply returned."
                        }
                    val requestsAtRelease = probe.requestsAtRelease.get()
                    val repliesAtRelease = probe.repliesAtRelease.get()
                    check(requestsAtRelease >= 0 && repliesAtRelease >= 0) {
                        "Cycle $cycle did not record the callback counts at native release."
                    }
                    expectStale("post-release managed operation begin") {
                        NetLiftHost.begin(
                            contract.ordersServiceId,
                            contract.placeOrderMethodId,
                            token,
                            NetLiftFlatDtoWire.encodeDispatchRequest(request(cycle))
                        )
                    }
                    postReleaseBeginRejections++
                    check(
                        requestCallbacks.get() == requestsAtRelease &&
                            replyCalls.get() == repliesAtRelease
                    ) {
                        "Cycle $cycle delivered a request/reply callback after native release."
                    }
                    expectStale("duplicate drain ack after release") {
                        NetLiftHost.ackDrain(token, generation)
                    }
                    duplicateAckRejections.incrementAndGet()
                    activeClient = null
                    activeReplyGate = null
                    if ((cycle + 1) % ProgressInterval == 0) {
                        val elapsed = (System.nanoTime() - startedAt) / 1_000_000
                        val message = "Real JNI V2 stress: ${cycle + 1}/$TotalCycles registrations, " +
                            "${requestCallbacks.get()} managed-origin requests, ${releases.get()} releases; " +
                            "${elapsed}ms"
                        Log.i("NetLiftSecondApp", message)
                        onProgress(message)
                    }
                }
            }
        } finally {
            withContext(NonCancellable) {
                activeReplyGate?.countDown()
                try {
                    activeClient?.let { client ->
                        client.requestClose()
                        withTimeout(DrainTimeoutMillis) { client.awaitClosed() }
                    }
                } finally {
                    callbackScope.cancel()
                }
            }
        }

        val elapsedMillis = (System.nanoTime() - startedAt) / 1_000_000
        check(idleCloses + acceptedDispatches + pendingRequestRevokes + claimedReplyRevokes == TotalCycles)
        check(registrations == TotalCycles)
        check(generationReuseTransitions == TotalCycles - 1)
        check(requestCallbacks.get() == AcceptedCycles + PendingCycles + ClaimedReplyCycles)
        check(claimedReplies.get() == AcceptedCycles + ClaimedReplyCycles)
        check(replyCalls.get() == claimedReplies.get())
        check(revokeCallbacks.get() == TotalCycles)
        check(drainAcks.get() == TotalCycles)
        check(releases.get() == TotalCycles)
        check(staleAckRejections.get() == TotalCycles)
        check(duplicateAckRejections.get() == TotalCycles)
        check(duplicateRevokeRejections.get() == TotalCycles)
        check(injectedStaleCallbacks.get() == 10 && injectedDuplicateCallbacks.get() == 10)
        check(reentrantIdleAcks == IdleCycles)
        check(postReleaseBeginRejections == TotalCycles)
        System.gc()
        val pssAfterKb = Debug.getPss()

        Summary(
            cycles = TotalCycles,
            registrations = registrations,
            generationReuseTransitions = generationReuseTransitions,
            idleCloses = idleCloses,
            acceptedDispatches = acceptedDispatches,
            pendingRequestRevokes = pendingRequestRevokes,
            claimedReplyRevokes = claimedReplyRevokes,
            requestCallbacks = requestCallbacks.get(),
            claimedReplies = claimedReplies.get(),
            replyCalls = replyCalls.get(),
            revokeCallbacks = revokeCallbacks.get(),
            drainAcks = drainAcks.get(),
            releases = releases.get(),
            staleAckRejections = staleAckRejections.get(),
            duplicateAckRejections = duplicateAckRejections.get(),
            duplicateRevokeRejections = duplicateRevokeRejections.get(),
            injectedStaleCallbacks = injectedStaleCallbacks.get(),
            injectedDuplicateCallbacks = injectedDuplicateCallbacks.get(),
            replyErrorsAfterRevoke = replyErrorsAfterRevoke.get(),
            reentrantIdleAcks = reentrantIdleAcks,
            postReleaseBeginRejections = postReleaseBeginRejections,
            pid = android.os.Process.myPid(),
            pssBeforeKb = pssBeforeKb,
            pssAfterKb = pssAfterKb,
            elapsedMillis = elapsedMillis
        )
    }

    private enum class Mode { IDLE, ACCEPT, PENDING, CLAIMED_REPLY }

    private class Probe(
        private val requestCount: AtomicInteger,
        private val claimedReplyCount: AtomicInteger,
        private val replyCallCount: AtomicInteger,
        private val totalRevokeCount: AtomicInteger,
        private val totalAckCount: AtomicInteger,
        private val totalReleaseCount: AtomicInteger,
        private val replyErrorsAfterRevoke: AtomicInteger,
        private val replyClaimed: CompletableDeferred<Unit>,
        private val allowReplyCall: CountDownLatch,
        private val holdReplyCall: Boolean
    ) : SecondContractClientObserver {
        val eventOrder = java.util.Collections.synchronizedList(mutableListOf<String>())
        val lastReplyFailure = java.util.concurrent.atomic.AtomicBoolean()
        val revokeCount = AtomicInteger()
        val ackCount = AtomicInteger()
        val releaseCount = AtomicInteger()
        val requestsAtRelease = AtomicInteger(-1)
        val repliesAtRelease = AtomicInteger(-1)

        override fun onRequest(registrationToken: Long, requestToken: Long) {
            requestCount.incrementAndGet()
        }

        override fun onReplyClaimed(registrationToken: Long, requestToken: Long) {
            claimedReplyCount.incrementAndGet()
            if (holdReplyCall) {
                replyClaimed.complete(Unit)
                check(allowReplyCall.await(DrainTimeoutMillis, TimeUnit.MILLISECONDS)) {
                    "Timed out waiting to release the claimed JNI reply."
                }
            }
        }

        override fun onReplyCallStarted(registrationToken: Long, requestToken: Long) {
            replyCallCount.incrementAndGet()
            eventOrder.add("reply-start")
        }

        override fun onReplyReturned(
            registrationToken: Long,
            requestToken: Long,
            failure: Exception?
        ) {
            lastReplyFailure.set(failure != null)
            if (failure != null)
                replyErrorsAfterRevoke.incrementAndGet()
            eventOrder.add("reply-return")
        }

        override fun onRevoke(registrationToken: Long, generation: Long) {
            totalRevokeCount.incrementAndGet()
            revokeCount.incrementAndGet()
            eventOrder.add("revoke")
        }

        override fun onDrainAck(registrationToken: Long, generation: Long) {
            totalAckCount.incrementAndGet()
            ackCount.incrementAndGet()
            eventOrder.add("ack")
        }

        override fun onRelease(registrationToken: Long) {
            requestsAtRelease.set(requestCount.get())
            repliesAtRelease.set(replyCallCount.get())
            totalReleaseCount.incrementAndGet()
            releaseCount.incrementAndGet()
            eventOrder.add("release")
        }
    }

    private fun request(sequence: Int) =
        DispatchRequest(channel = "jni-stress", sequence = sequence.toLong())

    private inline fun expectStale(label: String, action: () -> Unit) {
        try {
            action()
            error("$label was unexpectedly accepted.")
        } catch (failure: NativeAbiException) {
            check(failure.statusCode == 10) {
                "$label returned status ${failure.statusCode}, expected stale-handle status 10."
            }
        }
    }
}
