package com.netlift.secondcontract

import com.netlift.androidruntime.LiftContract
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.NetLiftNativeRegistrationV2
import com.netlift.androidruntime.NetLiftTypedReply
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicBoolean

internal class SecondContractClientLifecycleTest {
    @Test
    fun reentrantCloseDrainsCallbackAndNeverRepliesAfterRelease() = runBlocking {
        val abi = FakeAbi()
        lateinit var client: SecondContractClient
        val awaitRejected = AtomicBoolean()
        client = newClient(abi, CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)) {
            client.requestClose()
            try {
                client.awaitClosed()
            } catch (_: IllegalStateException) {
                awaitRejected.set(true)
            }
            true
        }
        val operation = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined).async(
            start = CoroutineStart.UNDISPATCHED
        ) {
            client.dispatch(com.netlift.androidhost.contract.DispatchRequest("test", 1))
        }

        abi.emitRequest(101)

        val failure = try {
            withTimeout(2_000) {
                operation.await()
            }
            null
        } catch (timeout: TimeoutCancellationException) {
            throw timeout
        } catch (error: Exception) {
            error
        }
        assertNotNull("The released operation must complete with an error.", failure)
        assertTrue("A callback must not await its own drain.", awaitRejected.get())
        withTimeout(2_000) { client.awaitClosed() }
        assertEquals(1, abi.ackCount.get())
        assertTrue("A revoked callback must not send a normal reply.", abi.replies.isEmpty())

        abi.emitRequest(102)
        assertTrue("A final release must close callback admission.", abi.replies.isEmpty())
    }

    @Test
    fun claimedReplyFinishesBeforeDrainAcknowledgement() = runBlocking {
        val abi = FakeAbi()
        val replyEntered = CountDownLatch(1)
        val allowReplyReturn = CountDownLatch(1)
        abi.replyEntered = replyEntered
        abi.allowReplyReturn = allowReplyReturn
        val client = newClient(
            abi,
            CoroutineScope(SupervisorJob() + Dispatchers.Default)
        ) { true }
        val callbackThread = Thread {
            abi.emitRequest(201)
        }.apply { start() }

        assertTrue(
            "The typed reply call did not begin.",
            withContext(Dispatchers.IO) { replyEntered.await(2, TimeUnit.SECONDS) }
        )
        client.requestClose()
        client.requestClose()
        assertEquals("Drain must wait for an already claimed reply.", 0, abi.ackCount.get())
        assertEquals("Close requests must revoke at most once.", 1, abi.revokeCount.get())

        allowReplyReturn.countDown()
        callbackThread.join(2_000)
        assertTrue("The request callback did not finish.", !callbackThread.isAlive)
        withTimeout(2_000) { client.awaitClosed() }
        assertEquals(1, abi.ackCount.get())
        assertTrue(
            "The native release must follow the claimed reply and its drain acknowledgement.",
            abi.events.indexOf("reply-return") < abi.events.indexOf("ack") &&
                abi.events.indexOf("ack") < abi.events.indexOf("release")
        )
    }

    @Test
    fun staleAndDuplicateRevokeIdentitiesDoNotAcknowledgeDrain() = runBlocking {
        val abi = FakeAbi()
        val client = newClient(
            abi,
            CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        ) { true }
        val registration = abi.registration

        abi.callbacks.onRevoke(registration.token, registration.generation + 1)
        assertEquals(0, abi.ackCount.get())
        client.requestClose()
        withTimeout(2_000) { client.awaitClosed() }
        abi.callbacks.onRevoke(registration.token, registration.generation)

        assertEquals(1, abi.ackCount.get())
        assertEquals(1, abi.releaseCount.get())
    }

    @Test
    fun registrationReturnRaceDefersReleaseUntilRevokeCallbackReturns() = runBlocking {
        val abi = FakeAbi(revokeDuringRegistration = true)
        val client = newClient(
            abi,
            CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        ) { true }

        withTimeout(2_000) { client.awaitClosed() }
        client.requestClose()

        assertEquals(1, abi.ackCount.get())
        assertEquals(1, abi.releaseCount.get())
        assertTrue(
            "Reentrant AckDrain must not deliver release inside OnRevoke.",
            abi.events.indexOf("revoke-return") < abi.events.indexOf("release")
        )
    }

    @Test
    fun aNewGenerationCanRegisterAfterThePriorClientDrains() = runBlocking {
        val abi = FakeAbi()
        val callbackScope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val first = newClient(abi, callbackScope) { true }
        val firstRegistration = abi.registration
        first.requestClose()
        withTimeout(2_000) { first.awaitClosed() }

        val second = newClient(abi, callbackScope) { true }
        val secondRegistration = abi.registration
        second.requestClose()
        withTimeout(2_000) { second.awaitClosed() }

        assertTrue(firstRegistration.token != secondRegistration.token)
        assertTrue(firstRegistration.generation != secondRegistration.generation)
        assertEquals(2, abi.ackCount.get())
        assertEquals(2, abi.releaseCount.get())
    }

    @Test
    fun malformedResultAndErrorDiagnosticsCompletePendingOperations() = runBlocking {
        val cases = listOf(
            Triple(8, byteArrayOf(), byteArrayOf(0xc3.toByte(), 0x28)),
            Triple(0, byteArrayOf(1), byteArrayOf())
        )
        for ((index, case) in cases.withIndex()) {
            val (status, result, error) = case
            val abi = FakeAbi()
            abi.onActivate = { callbacks, token ->
                callbacks.onResult(token, status, result, error)
            }
            val client = newClient(
                abi,
                CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
            ) { true }
            val failure = try {
                withTimeout(2_000) {
                    client.dispatch(com.netlift.androidhost.contract.DispatchRequest("test", 2))
                }
                null
            } catch (timeout: TimeoutCancellationException) {
                throw timeout
            } catch (exception: Exception) {
                exception
            }
            assertNotNull("Malformed native result must fail the dispatch.", failure)
            if (index == 0)
                assertTrue(failure?.message?.contains("invalid UTF-8") == true)
            client.requestClose()
            withTimeout(2_000) { client.awaitClosed() }
        }
    }

    @Test
    fun tenThousandMixedRequestRevokeAndDrainCyclesHaveNoLeaks() = runBlocking {
        val cycles = 10_000
        val callbackScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val activeJobs = AtomicInteger()
        val cancelledApprovals = AtomicInteger()
        var cancelledBeforeReply = 0
        var repliedBeforeRevoke = 0
        var revokedAfterReplyClaim = 0
        var totalReplies = 0
        var totalAcks = 0
        var totalReleases = 0
        val reportedErrors = AtomicInteger()

        try {
            repeat(cycles) { cycle ->
                val mode = cycle % 3
                val abi = FakeAbi()
                val approvalStarted = kotlinx.coroutines.CompletableDeferred<Unit>()
                val allowApproval = kotlinx.coroutines.CompletableDeferred<Unit>()
                val client = newClient(
                    abi = abi,
                    callbackScope = callbackScope,
                    approve = {
                        activeJobs.incrementAndGet()
                        approvalStarted.complete(Unit)
                        try {
                            if (mode != 1)
                                allowApproval.await()
                            true
                        } catch (cancelled: CancellationException) {
                            cancelledApprovals.incrementAndGet()
                            throw cancelled
                        } finally {
                            activeJobs.decrementAndGet()
                        }
                    },
                    onError = { reportedErrors.incrementAndGet() }
                )
                val registration = abi.registration
                if (mode == 2) {
                    abi.replyEntered = CountDownLatch(1)
                    abi.allowReplyReturn = CountDownLatch(1)
                }

                abi.callbacks.onRevoke(registration.token, registration.generation + 1)
                assertEquals("Stale generation acknowledged at cycle $cycle.", 0, abi.ackCount.get())
                assertEquals("Stale generation changed release state at cycle $cycle.", 0, abi.releaseCount.get())

                abi.emitRequest(10_000L + cycle)
                withTimeout(10_000) { approvalStarted.await() }

                when (mode) {
                    0 -> {
                        client.requestClose()
                        withTimeout(10_000) { client.awaitClosed() }
                        cancelledBeforeReply++
                    }
                    1 -> {
                        withTimeout(10_000) { abi.replyReturned.await() }
                        client.requestClose()
                        withTimeout(10_000) { client.awaitClosed() }
                        repliedBeforeRevoke++
                    }
                    else -> {
                        allowApproval.complete(Unit)
                        assertTrue(
                            "Cycle $cycle did not claim a typed reply.",
                            withContext(Dispatchers.IO) {
                                abi.replyEntered!!.await(10, TimeUnit.SECONDS)
                            }
                        )
                        client.requestClose()
                        assertEquals("Drain ack passed a claimed reply at cycle $cycle.", 0, abi.ackCount.get())
                        abi.allowReplyReturn!!.countDown()
                        withTimeout(10_000) { client.awaitClosed() }
                        revokedAfterReplyClaim++
                    }
                }

                assertEquals("Cycle $cycle did not acknowledge drain exactly once.", 1, abi.ackCount.get())
                assertEquals("Cycle $cycle did not release exactly once.", 1, abi.releaseCount.get())
                assertEquals("Cycle $cycle did not revoke exactly once.", 1, abi.revokeCount.get())
                assertEquals("Cycle $cycle did not emit exactly one request.", 1, abi.requestCount.get())
                assertEquals("Cycle $cycle retained an active callback job.", 0, activeJobs.get())
                assertEquals("Cycle $cycle retained an active native reply.", 0, abi.activeReplies.get())
                assertEquals("Cycle $cycle retained a registration lease.", 0, abi.outstandingLeases.get())
                assertEquals("Cycle $cycle did not deliver one reply.", if (mode == 0) 0 else 1, abi.replies.size)
                assertTrue(
                    "Cycle $cycle released before its drain acknowledgement.",
                    abi.events.indexOf("ack") >= 0 &&
                        abi.events.indexOf("ack") < abi.events.indexOf("release")
                )
                assertTrue(
                    "Cycle $cycle released before OnRevoke returned.",
                    abi.events.indexOf("revoke-return") >= 0 &&
                        abi.events.indexOf("revoke-return") < abi.events.indexOf("release")
                )
                totalReplies += abi.replies.size
                totalAcks += abi.ackCount.get()
                totalReleases += abi.releaseCount.get()
            }
        } finally {
            callbackScope.cancel()
        }

        assertEquals(cycles, cancelledBeforeReply + repliedBeforeRevoke + revokedAfterReplyClaim)
        assertEquals(cycles, cancelledApprovals.get() + totalReplies)
        assertEquals(cycles, totalAcks)
        assertEquals(cycles, totalReleases)
        assertEquals("Each stale-generation probe must be rejected.", cycles, reportedErrors.get())
    }

    private fun newClient(
        abi: FakeAbi,
        callbackScope: CoroutineScope,
        onError: (Exception) -> Unit = {},
        approve: suspend (String) -> Boolean
    ) = SecondContractClient(
        contract = LiftContract(
            assemblyName = "NetLift.AndroidRuntime.SecondContract",
            abiVersion = 1,
            requiredFeatureBits = 6,
            schemaHash = ByteArray(32) { it.toByte() },
            ordersServiceId = 41,
            placeOrderMethodId = 1,
            confirmationServiceId = 42,
            confirmationMethodId = 1,
            managedAssetDirectory = "netlift/apps/com.netlift.secondcontract/dotnet"
        ),
        callbackScope = callbackScope,
        approve = approve,
        abi = abi,
        onError = onError
    )

    private class FakeAbi(
        private val revokeDuringRegistration: Boolean = false
    ) : SecondContractNativeAbi {
        private val gate = Any()
        private var callbackDepth = 0
        private var ackReceived = false
        private var nextRegistrationToken = 700L
        private var nextGeneration = 18L
        private var nextOperationToken = 900L
        private val releasedTokens = mutableSetOf<Long>()
        val events = CopyOnWriteArrayList<String>()
        val ackCount = AtomicInteger()
        val revokeCount = AtomicInteger()
        val releaseCount = AtomicInteger()
        val requestCount = AtomicInteger()
        val activeReplies = AtomicInteger()
        val outstandingLeases = AtomicInteger()
        val replies = CopyOnWriteArrayList<NetLiftTypedReply>()
        val replyReturned = kotlinx.coroutines.CompletableDeferred<Unit>()
        lateinit var callbacks: NetLiftNativeCallbacksV2
            private set
        lateinit var registration: NetLiftNativeRegistrationV2
            private set
        var replyEntered: CountDownLatch? = null
        var allowReplyReturn: CountDownLatch? = null
        var onActivate: ((NetLiftNativeCallbacksV2, Long) -> Unit)? = null

        override fun registerCallbacksV2(
            serviceId: Int,
            callbacks: NetLiftNativeCallbacksV2
        ): NetLiftNativeRegistrationV2 {
            assertEquals(42, serviceId)
            val registered = synchronized(gate) {
                this.callbacks = callbacks
                NetLiftNativeRegistrationV2(
                    ++nextRegistrationToken,
                    ++nextGeneration
                ).also {
                    registration = it
                    outstandingLeases.incrementAndGet()
                    ackReceived = false
                }
            }
            if (revokeDuringRegistration)
                invokeRevoke(registered.token, registered.generation)
            return registered
        }

        override fun revokeV2(registrationToken: Long) {
            val registered = synchronized(gate) {
                assertEquals(registration.token, registrationToken)
                revokeCount.incrementAndGet()
                registration
            }
            invokeRevoke(registered.token, registered.generation)
        }

        private fun invokeRevoke(token: Long, generation: Long) {
            val target = synchronized(gate) {
                events.add("revoke-enter")
                callbackDepth++
                callbacks
            }
            try {
                target.onRevoke(token, generation)
            } finally {
                synchronized(gate) {
                    events.add("revoke-return")
                    callbackDepth--
                }
            }
            deliverReleaseIfDrained()
        }

        override fun ackDrain(registrationToken: Long, generation: Long) {
            synchronized(gate) {
                assertEquals(registration.token, registrationToken)
                assertEquals(registration.generation, generation)
                ackCount.incrementAndGet()
                events.add("ack")
                ackReceived = true
            }
            deliverReleaseIfDrained()
        }

        private fun deliverReleaseIfDrained() {
            val tokenToRelease = synchronized(gate) {
                registration.token.takeIf {
                    callbackDepth == 0 && ackReceived && releasedTokens.add(it)
                }?.also {
                    releaseCount.incrementAndGet()
                    events.add("release")
                    outstandingLeases.decrementAndGet()
                }?.let { it to callbacks }
            } ?: return
            tokenToRelease.second.onRelease(tokenToRelease.first)
        }

        override fun begin(
            serviceId: Int,
            methodId: Int,
            registrationToken: Long,
            arguments: ByteArray
        ): Long {
            return synchronized(gate) {
                assertEquals(41, serviceId)
                assertEquals(1, methodId)
                assertEquals(registration.token, registrationToken)
                ++nextOperationToken
            }
        }

        override fun activate(operationToken: Long) {
            onActivate?.invoke(callbacks, operationToken)
        }

        override fun cancel(operationToken: Long) {
            events.add("cancel:$operationToken")
        }

        override fun replyTyped(
            registrationToken: Long,
            requestToken: Long,
            reply: NetLiftTypedReply
        ) {
            synchronized(gate) {
                assertEquals(registration.token, registrationToken)
            }
            activeReplies.incrementAndGet()
            try {
                replyEntered?.countDown()
                events.add("reply-enter")
                check(allowReplyReturn?.await(10, TimeUnit.SECONDS) != false) {
                    "Timed out while holding the typed reply call."
                }
                replies.add(reply)
                events.add("reply-return")
                replyReturned.complete(Unit)
            } finally {
                activeReplies.decrementAndGet()
            }
        }

        fun emitRequest(requestToken: Long) {
            requestCount.incrementAndGet()
            val requestTarget = synchronized(gate) { callbacks to registration.token }
            requestTarget.first.onRequest(
                requestTarget.second,
                requestToken,
                1,
                SecondContractWire.encodeApprovalPrompt("Approve test dispatch?")
            )
        }
    }
}
