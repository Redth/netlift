package com.netlift.androidhost.contract

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

private class GeneratedFacadeFakeAbi(
    private val deliverRequest: Boolean = true,
    private val requestMethodId: Int = NetLiftDispatchContract.NATIVE_METHOD_ID,
    private val requestPayload: ByteArray = NetLiftFlatDtoWire.encodeStringField(1, "Approve north-star")
) : LiftNativeCallbackLifecycleV2Abi {
    private val gate = Any()
    @Volatile private var callbacks: LiftNativeCallbacksV2? = null
    private var operationToken = 23L
    private var revoking = false
    private var drainAcknowledged = false
    private var released = false
    private var callbacksInFlight = 0
    private var repliesInFlight = 0
    private val requestToken = 17L

    val activated = CountDownLatch(1)
    val activationCount = AtomicInteger()
    val cancelCount = AtomicInteger()
    val replyCount = AtomicInteger()
    val revokeCount = AtomicInteger()
    val ackCount = AtomicInteger()
    val releaseCount = AtomicInteger()
    @Volatile var lastReply: LiftNativeReply? = null
        private set

    override fun start(abiVersion: Int, schemaHash: ByteArray): LiftNativeRuntimeInfo {
        check(abiVersion == NetLiftDispatchContract.ABI_VERSION)
        check(schemaHash.contentEquals(NetLiftDispatchContract.schemaHash))
        return LiftNativeRuntimeInfo(
            abiVersion,
            NetLiftDispatchContract.FEATURE_ASYNC_CALLBACKS or
                NetLiftDispatchContract.FEATURE_TYPED_NATIVE_REPLY,
            schemaHash.copyOf())
    }

    override fun registerCallbacks(
        serviceId: Int,
        callbacks: LiftNativeCallbacks
    ): Long = error("The generated V2 facade must not use V1 callback registration.")

    override fun registerCallbacksV2(
        serviceId: Int,
        callbacks: LiftNativeCallbacksV2
    ): LiftNativeRegistrationV2 {
        check(serviceId == NetLiftDispatchContract.NATIVE_SERVICE_ID)
        this.callbacks = callbacks
        return LiftNativeRegistrationV2(11L, 0x0000000b00000001L)
    }

    override fun revoke(registrationToken: Long) =
        error("The generated V2 facade must not use V1 revoke.")

    override fun revokeV2(registrationToken: Long) {
        check(registrationToken == 11L)
        synchronized(gate) {
            check(!revoking)
            revoking = true
        }
        revokeCount.incrementAndGet()
        withCallback {
            callbacks!!.onRevoke(registrationToken, 0x0000000b00000001L)
        }
    }

    override fun ackDrain(registrationToken: Long, generation: Long) {
        check(registrationToken == 11L && generation == 0x0000000b00000001L)
        synchronized(gate) {
            check(revoking && !drainAcknowledged)
            drainAcknowledged = true
        }
        ackCount.incrementAndGet()
        maybeRelease()
    }

    override fun begin(
        serviceId: Int,
        methodId: Int,
        registrationToken: Long,
        arguments: ByteArray
    ): Long {
        check(serviceId == NetLiftDispatchContract.MANAGED_SERVICE_ID)
        check(methodId == NetLiftDispatchContract.MANAGED_METHOD_ID)
        check(registrationToken == 11L)
        check(NetLiftFlatDtoWire.decodeDispatchRequest(arguments) ==
            DispatchRequest("north-star", 73L))
        return operationToken
    }

    override fun activate(operationToken: Long) {
        check(operationToken == this.operationToken)
        activationCount.incrementAndGet()
        activated.countDown()
        if (deliverRequest) {
            withCallback {
                callbacks!!.onRequest(
                    11L,
                    requestToken,
                    requestMethodId,
                    requestPayload.copyOf())
            }
        }
    }

    fun completeOperation() {
        val result = NetLiftFlatDtoWire.encodeDispatchReceipt(
            DispatchReceipt(1L, "north-star", 73L, true))
        withCallback {
            callbacks!!.onResult(operationToken, 0, result, byteArrayOf())
        }
    }

    override fun cancel(operationToken: Long) {
        check(operationToken == this.operationToken)
        cancelCount.incrementAndGet()
        withCallback {
            callbacks!!.onResult(operationToken, 12, byteArrayOf(), byteArrayOf())
        }
    }

    override fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean) =
        error("The generated V2 facade must not use legacy bool replies.")

    override fun replyTyped(registrationToken: Long, requestToken: Long, reply: LiftNativeReply) {
        check(registrationToken == 11L && requestToken == this.requestToken)
        synchronized(gate) {
            check(!released)
            repliesInFlight++
        }
        replyCount.incrementAndGet()
        lastReply = reply
        try {
            val result = if (reply.outcome == LiftNativeReplyOutcome.RESULT && reply.accepted) {
                NetLiftFlatDtoWire.encodeDispatchReceipt(
                    DispatchReceipt(1L, "north-star", 73L, true))
            } else {
                byteArrayOf()
            }
            val (status, error) = when {
                reply.outcome == LiftNativeReplyOutcome.CANCELLED -> 12 to byteArrayOf()
                reply.outcome == LiftNativeReplyOutcome.HANDLER_FAILURE ->
                    13 to reply.diagnostic.copyOf()
                reply.outcome != LiftNativeReplyOutcome.RESULT ->
                    7 to reply.diagnostic.copyOf()
                !reply.accepted -> 8 to "Approval declined.".toByteArray()
                else -> 0 to byteArrayOf()
            }
            withCallback {
                callbacks!!.onResult(operationToken, status, result, error)
            }
        } finally {
            synchronized(gate) { repliesInFlight-- }
            maybeRelease()
        }
    }

    fun emitLateCallbacks() {
        callbacks!!.onRequest(
            11L,
            99L,
            NetLiftDispatchContract.NATIVE_METHOD_ID,
            requestPayload.copyOf())
        callbacks!!.onResult(operationToken, 0, byteArrayOf(), byteArrayOf())
    }

    private inline fun <T> withCallback(action: () -> T): T {
        synchronized(gate) { callbacksInFlight++ }
        try {
            return action()
        } finally {
            synchronized(gate) { callbacksInFlight-- }
            maybeRelease()
        }
    }

    private fun maybeRelease() {
        val shouldRelease = synchronized(gate) {
            if (!revoking || !drainAcknowledged || callbacksInFlight != 0 ||
                repliesInFlight != 0 || released)
                false
            else {
                released = true
                true
            }
        }
        if (!shouldRelease)
            return
        releaseCount.incrementAndGet()
        callbacks!!.onRelease(11L)
    }
}

private fun newGeneratedClient(
    abi: GeneratedFacadeFakeAbi,
    approve: suspend (String) -> Boolean,
    errors: MutableList<Exception>
) = DispatchClient(
    abi,
    CoroutineScope(SupervisorJob() + Dispatchers.Default),
    approve,
    { errors += it })

fun main() = runBlocking {
    check(NetLiftDispatchContract.SCHEMA_HASH_HEX.length == 64)
    check(NetLiftFlatDtoWire.decodeStringField(
        NetLiftFlatDtoWire.encodeStringField(1, "approval"), 1) == "approval")

    suspend fun close(client: DispatchClient, abi: GeneratedFacadeFakeAbi) {
        client.requestClose()
        withTimeout(2_000) { client.awaitClosed() }
        check(abi.ackCount.get() == 1 && abi.releaseCount.get() == 1)
    }

    run {
        val abi = GeneratedFacadeFakeAbi()
        val errors = CopyOnWriteArrayList<Exception>()
        val client = newGeneratedClient(abi, { prompt ->
            check(prompt == "Approve north-star")
            true
        }, errors)
        val receipt = client.dispatchAsync(DispatchRequest("north-star", 73L))
        check(receipt == DispatchReceipt(1L, "north-star", 73L, true))
        check(abi.lastReply?.outcome == LiftNativeReplyOutcome.RESULT &&
            abi.lastReply?.accepted == true)
        close(client, abi)
        check(errors.isEmpty())
    }

    run {
        val abi = GeneratedFacadeFakeAbi(deliverRequest = false)
        val client = newGeneratedClient(abi, { true }, CopyOnWriteArrayList())
        val original = async {
            client.dispatchAsync(DispatchRequest("north-star", 73L))
        }
        check(withTimeout(2_000) {
            kotlinx.coroutines.withContext(Dispatchers.IO) {
                abi.activated.await(2, TimeUnit.SECONDS)
            }
        })

        val duplicateFailure = runCatching {
            client.dispatchAsync(DispatchRequest("north-star", 73L))
        }.exceptionOrNull()
        check(duplicateFailure is IllegalStateException &&
            duplicateFailure.message?.contains("reused a live operation token") == true)
        check(abi.cancelCount.get() == 0 && abi.activationCount.get() == 1)

        abi.completeOperation()
        check(withTimeout(2_000) { original.await() } ==
            DispatchReceipt(1L, "north-star", 73L, true))
        check(abi.cancelCount.get() == 0)
        close(client, abi)
    }

    run {
        val abi = GeneratedFacadeFakeAbi()
        val errors = CopyOnWriteArrayList<Exception>()
        val client = newGeneratedClient(abi, { false }, errors)
        val failure = runCatching {
            client.dispatchAsync(DispatchRequest("north-star", 73L))
        }.exceptionOrNull()
        check(failure is LiftAbiException && failure.statusCode == 8)
        check(abi.lastReply?.outcome == LiftNativeReplyOutcome.RESULT &&
            abi.lastReply?.accepted == false)
        close(client, abi)
    }

    run {
        val abi = GeneratedFacadeFakeAbi()
        val client = newGeneratedClient(
            abi,
            { throw CancellationException("handler cancelled") },
            CopyOnWriteArrayList())
        val failure = runCatching {
            client.dispatchAsync(DispatchRequest("north-star", 73L))
        }.exceptionOrNull()
        check(failure is CancellationException)
        check(abi.lastReply?.outcome == LiftNativeReplyOutcome.CANCELLED)
        close(client, abi)
    }

    run {
        val abi = GeneratedFacadeFakeAbi()
        val client = newGeneratedClient(
            abi,
            { throw IllegalStateException("approval failed") },
            CopyOnWriteArrayList())
        val failure = runCatching {
            client.dispatchAsync(DispatchRequest("north-star", 73L))
        }.exceptionOrNull()
        check(failure is LiftAbiException && failure.statusCode == 13)
        check(abi.lastReply?.outcome == LiftNativeReplyOutcome.HANDLER_FAILURE)
        close(client, abi)
    }

    run {
        val abi = GeneratedFacadeFakeAbi(deliverRequest = false)
        val client = newGeneratedClient(abi, { true }, CopyOnWriteArrayList())
        val pending = async {
            runCatching {
                client.dispatchAsync(DispatchRequest("north-star", 73L))
            }.exceptionOrNull()
        }
        check(withTimeout(2_000) {
            kotlinx.coroutines.withContext(Dispatchers.IO) {
                abi.activated.await(2, TimeUnit.SECONDS)
            }
        })
        pending.cancel()
        withTimeout(2_000) { pending.join() }
        check(abi.cancelCount.get() == 1)
        close(client, abi)
    }

    run {
        val abi = GeneratedFacadeFakeAbi()
        val entered = CompletableDeferred<Unit>()
        val errors = CopyOnWriteArrayList<Exception>()
        lateinit var client: DispatchClient
        client = newGeneratedClient(abi, {
            try {
                client.awaitClosed()
                error("awaitClosed unexpectedly completed inside its own callback")
            } catch (_: IllegalStateException) {
                entered.complete(Unit)
            }
            awaitCancellation()
        }, errors)
        val pending = async {
            runCatching {
                client.dispatchAsync(DispatchRequest("north-star", 73L))
            }.exceptionOrNull()
        }
        withTimeout(2_000) { entered.await() }
        client.requestClose()
        val failure = withTimeout(2_000) { pending.await() }
        check(failure is LiftAbiException)
        withTimeout(2_000) { client.awaitClosed() }
        check(abi.replyCount.get() == 0)
        abi.emitLateCallbacks()
        check(abi.replyCount.get() == 0)
        check(abi.revokeCount.get() == 1 && abi.ackCount.get() == 1 &&
            abi.releaseCount.get() == 1)
        check(errors.any { it.message?.contains("Late or duplicate") == true })
    }

    println("PASS Kotlin generated Dispatch facade: typed outcomes, cancellation, V2 revoke/drain, late callbacks, and external awaitClosed")
}
