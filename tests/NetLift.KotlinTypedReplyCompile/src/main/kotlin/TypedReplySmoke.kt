package com.netlift.androidhost.contract

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

private class TypedReplyFake(
    private val featureBits: Int =
        NetLiftOrdersContract.FEATURE_ASYNC_CALLBACKS or NetLiftOrdersContract.FEATURE_TYPED_NATIVE_REPLY,
    private val requestMethodId: Int = NetLiftOrdersContract.CONFIRM_ORDER_METHOD_ID,
    private val requestPayload: ByteArray = TypedReplyFake.confirmationPayload("Confirm order SKU-41 x 3"),
    private val runtimeHash: ByteArray? = null,
    private val runtimeVersion: Int = NetLiftOrdersContract.ABI_VERSION,
    private val revokeDuringRegistration: Boolean = false
) : LiftNativeCallbackLifecycleV2Abi {
    private val nativeGate = Any()
    private var callbacks: LiftNativeCallbacksV2? = null
    var reply: LiftNativeReply? = null
    private var operationToken = 23L
    val requestEntered = CountDownLatch(1)
    @Volatile var replyEntered: CountDownLatch? = null
    @Volatile var replyGate: CountDownLatch? = null
    @Volatile var registrationReleased = false
        private set
    val repliesAfterRelease = AtomicInteger()
    val replyCount = AtomicInteger()
    val revokeCount = AtomicInteger()
    val ackCount = AtomicInteger()
    val releaseCount = AtomicInteger()
    @Volatile var ackObservedInsideOnRevoke = false
        private set
    private var activeNativeCallbacks = 0
    private var activeNativeReplies = 0
    private var revoking = false
    private var drainAcknowledged = false
    private var releaseSent = false
    private var requestTerminal = false
    private var replyClaimed = false

    override fun start(abiVersion: Int, schemaHash: ByteArray): LiftNativeRuntimeInfo {
        check(abiVersion == NetLiftOrdersContract.ABI_VERSION)
        check(schemaHash.contentEquals(expectedSchemaHash()))
        return LiftNativeRuntimeInfo(
            runtimeVersion,
            featureBits,
            runtimeHash?.copyOf() ?: schemaHash.copyOf())
    }

    override fun registerCallbacks(serviceId: Int, callbacks: LiftNativeCallbacks): Long =
        error("v1 callback registration must not be used by a lifecycle-v2 client")

    override fun registerCallbacksV2(
        serviceId: Int,
        callbacks: LiftNativeCallbacksV2
    ): LiftNativeRegistrationV2 {
        check(serviceId == NetLiftOrdersContract.CONFIRMATION_SERVICE_ID)
        this.callbacks = callbacks
        if (revokeDuringRegistration) revokeV2(11L)
        return LiftNativeRegistrationV2(11L, 0x0000000b00000001L)
    }

    override fun revoke(registrationToken: Long) =
        error("The lifecycle-v2 client must not use V1 revoke.")

    override fun revokeV2(registrationToken: Long) {
        check(registrationToken == 11L)
        revokeCount.incrementAndGet()
        val dispatch = synchronized(nativeGate) {
            if (revoking) false
            else {
                revoking = true
                if (!replyClaimed) requestTerminal = true
                activeNativeCallbacks++
                true
            }
        }
        if (!dispatch) return
        try {
            callbacks!!.onRevoke(registrationToken, 0x0000000b00000001L)
        } finally {
            ackObservedInsideOnRevoke = synchronized(nativeGate) { drainAcknowledged }
            check(!registrationReleased) {
                "final release ran reentrantly before the OnRevoke callback returned"
            }
            synchronized(nativeGate) { activeNativeCallbacks-- }
            maybeRelease()
        }
    }

    override fun ackDrain(registrationToken: Long, generation: Long) {
        check(registrationToken == 11L)
        check(generation == 0x0000000b00000001L)
        synchronized(nativeGate) {
            check(revoking) { "AckDrain before revoke" }
            check(!drainAcknowledged) { "AckDrain called more than once" }
            drainAcknowledged = true
        }
        ackCount.incrementAndGet()
        maybeRelease()
    }

    override fun begin(serviceId: Int, methodId: Int, registrationToken: Long, arguments: ByteArray): Long {
        check(serviceId == NetLiftOrdersContract.ORDERS_SERVICE_ID)
        check(methodId == NetLiftOrdersContract.PLACE_ORDER_METHOD_ID)
        check(registrationToken == 11L)
        check(arguments.isNotEmpty())
        return operationToken
    }

    override fun activate(operationToken: Long) {
        check(operationToken == this.operationToken)
        withNativeCallback {
            callbacks!!.onRequest(
                11L,
                17L,
                requestMethodId,
                requestPayload.copyOf())
        }
        requestEntered.countDown()
    }

    override fun cancel(operationToken: Long) {
        check(operationToken == this.operationToken)
        withNativeCallback {
            callbacks!!.onResult(operationToken, 12, byteArrayOf(), byteArrayOf())
        }
    }

    override fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean) {
        error("legacy bool reply must not be used by a schema-v3 client")
    }

    override fun replyTyped(registrationToken: Long, requestToken: Long, reply: LiftNativeReply) {
        check(registrationToken == 11L && requestToken == 17L)
        synchronized(nativeGate) {
            if (registrationReleased) repliesAfterRelease.incrementAndGet()
            check(!registrationReleased) { "native reply called after final release" }
            check(!releaseSent) { "native reply raced past final release" }
            check(!requestTerminal) { "reply lost the native terminal race" }
            check(!replyClaimed) { "more than one terminal reply was delivered" }
            replyClaimed = true
            activeNativeReplies++
        }
        replyCount.incrementAndGet()
        replyEntered?.countDown()
        try {
            check(replyGate?.await(5, TimeUnit.SECONDS) != false) { "native reply barrier timed out" }
            this.reply = reply
            val (status, result, error) = when (reply.outcome) {
                LiftNativeReplyOutcome.RESULT -> if (reply.accepted)
                    Triple(0, receiptPayload(), byteArrayOf())
                else
                    Triple(8, byteArrayOf(), "Order was declined.".toByteArray())
                LiftNativeReplyOutcome.CANCELLED -> Triple(12, byteArrayOf(), byteArrayOf())
                LiftNativeReplyOutcome.UNAVAILABLE -> Triple(11, byteArrayOf(), reply.diagnostic)
                LiftNativeReplyOutcome.MALFORMED_PAYLOAD -> Triple(7, byteArrayOf(), reply.diagnostic)
                LiftNativeReplyOutcome.UNKNOWN_METHOD -> Triple(3, byteArrayOf(), reply.diagnostic)
                LiftNativeReplyOutcome.HANDLER_FAILURE -> Triple(13, byteArrayOf(), reply.diagnostic)
                else -> error("unknown typed reply outcome ${reply.outcome}")
            }
            withNativeCallback {
                callbacks!!.onResult(operationToken, status, result, error)
            }
        } finally {
            synchronized(nativeGate) {
                activeNativeReplies--
                requestTerminal = true
            }
            maybeRelease()
        }
    }

    private inline fun <T> withNativeCallback(action: () -> T): T {
        synchronized(nativeGate) { activeNativeCallbacks++ }
        try {
            return action()
        } finally {
            synchronized(nativeGate) { activeNativeCallbacks-- }
            maybeRelease()
        }
    }

    private fun maybeRelease() {
        val release = synchronized(nativeGate) {
            if (!revoking || !drainAcknowledged || activeNativeCallbacks != 0 ||
                activeNativeReplies != 0 || !requestTerminal || releaseSent) false
            else {
                releaseSent = true
                true
            }
        }
        if (release) {
            registrationReleased = true
            releaseCount.incrementAndGet()
            callbacks!!.onRelease(11L)
        }
    }

    private fun expectedSchemaHash(): ByteArray =
        NetLiftOrdersContract.SCHEMA_HASH_HEX.chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    companion object {
        fun confirmationPayload(prompt: String): ByteArray {
            val payload = prompt.toByteArray(Charsets.UTF_8)
            return byteArrayOf(1, 0) + littleEndian(payload.size) + payload
        }

        private fun receiptPayload(): ByteArray {
            val sku = "SKU-41".toByteArray(Charsets.UTF_8)
            return byteArrayOf(1, 0) + littleEndian(8) + littleEndian(41L) +
                byteArrayOf(2, 0) + littleEndian(sku.size) + sku +
                byteArrayOf(3, 0) + littleEndian(4) + littleEndian(3)
        }

        private fun littleEndian(value: Int): ByteArray =
            ByteArray(4) { index -> (value ushr (index * 8)).toByte() }

        private fun littleEndian(value: Long): ByteArray =
            ByteArray(8) { index -> (value ushr (index * 8)).toByte() }
    }
}

private suspend fun verifyOutcome(
    expectedOutcome: Int,
    expectedStatus: Int,
    confirmation: suspend (NativeConfirmationRequest) -> Boolean,
    abi: TypedReplyFake = TypedReplyFake())
{
    val client = OrdersClient(
        abi,
        CoroutineScope(SupervisorJob() + Dispatchers.Default),
        confirmation,
        { })
    try {
        val receipt = client.placeAsync(OrderDraft("SKU-41", 3))
        check(expectedStatus == 0) { "unexpected receipt $receipt" }
        check(receipt == OrderReceipt(41, "SKU-41", 3))
    } catch (failure: LiftAbiException) {
        check(failure.statusCode == expectedStatus) {
            "expected ABI status $expectedStatus, got ${failure.statusCode}: ${failure.message}"
        }
    } catch (failure: kotlinx.coroutines.CancellationException) {
        check(expectedStatus == 12)
    } finally {
        client.close()
    }
    check(abi.reply?.outcome == expectedOutcome) {
        "expected typed outcome $expectedOutcome, got ${abi.reply?.outcome}"
    }
}

private fun dtoField(fieldId: Int, payload: ByteArray): ByteArray =
    byteArrayOf(fieldId.toByte(), (fieldId ushr 8).toByte()) +
        ByteArray(4) { index -> (payload.size ushr (index * 8)).toByte() } + payload

private fun dtoLong(value: Long): ByteArray =
    ByteArray(8) { index -> (value ushr (index * 8)).toByte() }

private fun dtoInt(value: Int): ByteArray =
    ByteArray(4) { index -> (value ushr (index * 8)).toByte() }

private fun expectInvalidDto(block: () -> Unit) {
    check(runCatching(block).exceptionOrNull() is IllegalArgumentException) {
        "invalid DTO wire input was accepted"
    }
}

fun main() = runBlocking {
    val dispatchRequest = DispatchRequest("Q-7", 0x0102030405060708L)
    val requestBytes = NetLiftFlatDtoWire.encodeDispatchRequest(dispatchRequest)
    check(requestBytes.contentEquals(
        dtoField(1, "Q-7".toByteArray(Charsets.UTF_8)) +
            dtoField(2, dtoLong(0x0102030405060708L)))) {
        "DispatchRequest bytes differ from the second-app field-ID/length wire format"
    }
    check(NetLiftFlatDtoWire.decodeDispatchRequest(requestBytes) == dispatchRequest)

    val dispatchReceipt = DispatchReceipt(42L, "sent", 9L, true)
    val receiptBytes = NetLiftFlatDtoWire.encodeDispatchReceipt(dispatchReceipt)
    val expectedReceiptBytes = dtoField(1, dtoLong(42L)) +
        dtoField(2, "sent".toByteArray(Charsets.UTF_8)) +
        dtoField(3, dtoLong(9L)) +
        dtoField(4, byteArrayOf(1))
    check(receiptBytes.contentEquals(expectedReceiptBytes)) {
        "DispatchReceipt bytes differ from the second-app field-ID/length wire format"
    }
    check(NetLiftFlatDtoWire.decodeDispatchReceipt(receiptBytes) == dispatchReceipt)

    val fixedWidthValues = FixedWidthValues(
        true,
        UByte.MAX_VALUE,
        -5,
        -1234,
        UShort.MAX_VALUE,
        Int.MIN_VALUE,
        UInt.MAX_VALUE,
        Long.MIN_VALUE,
        ULong.MAX_VALUE,
        -1.25f,
        0.5)
    check(NetLiftFlatDtoWire.decodeFixedWidthValues(
        NetLiftFlatDtoWire.encodeFixedWidthValues(fixedWidthValues)) == fixedWidthValues)

    expectInvalidDto { NetLiftFlatDtoWire.decodeDispatchRequest(byteArrayOf(1, 0)) }
    expectInvalidDto {
        NetLiftFlatDtoWire.decodeDispatchRequest(
            dtoField(2, dtoLong(4L)) + dtoField(1, "Q-7".toByteArray(Charsets.UTF_8)))
    }
    expectInvalidDto {
        NetLiftFlatDtoWire.decodeDispatchRequest(
            dtoField(1, "Q-7".toByteArray(Charsets.UTF_8)) +
                dtoField(1, dtoLong(4L)))
    }
    expectInvalidDto {
        NetLiftFlatDtoWire.decodeDispatchRequest(
            dtoField(1, "Q-7".toByteArray(Charsets.UTF_8)) +
                dtoField(2, dtoInt(4)))
    }
    expectInvalidDto { NetLiftFlatDtoWire.decodeDispatchRequest(requestBytes + byteArrayOf(0)) }
    expectInvalidDto {
        NetLiftFlatDtoWire.decodeDispatchRequest(
            dtoField(1, byteArrayOf(0xff.toByte())) + dtoField(2, dtoLong(4L)))
    }
    expectInvalidDto {
        NetLiftFlatDtoWire.encodeDispatchRequest(DispatchRequest("\uD800", 4L))
    }
    expectInvalidDto { NetLiftFlatDtoWire.decodeDispatchRequest(ByteArray(4097)) }
    expectInvalidDto {
        NetLiftFlatDtoWire.decodeDispatchRequest(
            byteArrayOf(1, 0, 0xff.toByte(), 0xff.toByte(), 0xff.toByte(), 0xff.toByte()) +
                dtoField(2, dtoLong(4L)))
    }
    expectInvalidDto {
        NetLiftFlatDtoWire.decodeDispatchReceipt(
            dtoField(1, dtoLong(42L)) +
                dtoField(2, "sent".toByteArray(Charsets.UTF_8)) +
                dtoField(3, dtoLong(9L)) +
                dtoField(4, byteArrayOf(2)))
    }
    expectInvalidDto {
        NetLiftFlatDtoWire.decodeDispatchReceipt(
            dtoField(1, dtoLong(42L)) +
                dtoField(2, "sent".toByteArray(Charsets.UTF_8)) +
                dtoField(3, dtoLong(9L)) +
                dtoField(4, byteArrayOf(0, 1)))
    }
    expectInvalidDto {
        NetLiftFlatDtoWire.encodeDispatchRequest(DispatchRequest("x".repeat(4080), 1L))
    }
    println("PASS Kotlin opt-in flat DTO codecs match the second-app wire bytes and reject malformed inputs")

    check(NetLiftOrdersContract.SCHEMA_HASH_HEX ==
        "d5e42aba3ef6561eb52b8f3fb835dcfa767b4835c6af8af9acd0a84769bb2db6")
    verifyOutcome(LiftNativeReplyOutcome.RESULT, 0, { true })
    verifyOutcome(LiftNativeReplyOutcome.RESULT, 8, { false })
    verifyOutcome(LiftNativeReplyOutcome.CANCELLED, 12, { throw kotlinx.coroutines.CancellationException() })
    verifyOutcome(
        LiftNativeReplyOutcome.HANDLER_FAILURE,
        13,
        { throw IllegalStateException("confirmation failed") })
    verifyOutcome(
        LiftNativeReplyOutcome.UNKNOWN_METHOD,
        3,
        { error("unknown method must not reach handler") },
        TypedReplyFake(requestMethodId = 999))
    verifyOutcome(
        LiftNativeReplyOutcome.MALFORMED_PAYLOAD,
        7,
        { error("malformed payload must not reach handler") },
        TypedReplyFake(requestPayload = byteArrayOf(1, 0, 1, 0, 0, 0, 0xff.toByte())))

    run {
        val abi = TypedReplyFake()
        val client = OrdersClient(abi, CoroutineScope(SupervisorJob() + Dispatchers.Default), { true }, { })
        val result = client.placeAsync(OrderDraft("SKU-41", 3))
        check(result == OrderReceipt(41, "SKU-41", 3))
        client.requestClose()
        client.awaitClosed()
        check(abi.replyCount.get() == 1 && abi.ackCount.get() == 1 && abi.releaseCount.get() == 1)
        println("PASS Kotlin lifecycle v2 claims reply before revoke and acknowledges once")
    }

    run {
        val abi = TypedReplyFake(revokeDuringRegistration = true)
        val client = OrdersClient(abi, CoroutineScope(SupervisorJob() + Dispatchers.Default), { true }, { })
        client.awaitClosed()
        check(abi.ackCount.get() == 1 && abi.releaseCount.get() == 1)
        println("PASS Kotlin lifecycle v2 validates generation when revoke races registration return")
    }

    run {
        val abi = TypedReplyFake()
        val handlerEntered = CountDownLatch(1)
        val cleanupEntered = CountDownLatch(1)
        val cleanupGate = kotlinx.coroutines.CompletableDeferred<Unit>()
        val client = OrdersClient(
            abi,
            CoroutineScope(SupervisorJob() + Dispatchers.Default),
            {
                handlerEntered.countDown()
                try {
                    kotlinx.coroutines.CompletableDeferred<Boolean>().await()
                } finally {
                    withContext(NonCancellable) {
                        cleanupEntered.countDown()
                        cleanupGate.await()
                    }
                }
            },
            { })
        val pending = async {
            runCatching { client.placeAsync(OrderDraft("SKU-41", 3)) }.exceptionOrNull()
        }
        withContext(Dispatchers.IO) {
            check(abi.requestEntered.await(5, TimeUnit.SECONDS))
            check(handlerEntered.await(5, TimeUnit.SECONDS))
        }
        client.requestClose()
        check(cleanupEntered.await(5, TimeUnit.SECONDS))
        check(!abi.registrationReleased && abi.ackCount.get() == 0) {
            "drain was acknowledged before asynchronous callback cleanup completed"
        }
        cleanupGate.complete(Unit)
        client.awaitClosed()
        check((pending.await() as? LiftAbiException)?.statusCode == 11)
        check(abi.replyCount.get() == 0 && abi.ackCount.get() == 1 && abi.releaseCount.get() == 1)
        println("PASS Kotlin lifecycle v2 revoke-before-claim cancels and drains handler cleanup")
    }

    run {
        val abi = TypedReplyFake()
        val replyEntered = CountDownLatch(1)
        val replyGate = CountDownLatch(1)
        abi.replyEntered = replyEntered
        abi.replyGate = replyGate
        val client = OrdersClient(abi, CoroutineScope(SupervisorJob() + Dispatchers.Default), { true }, { })
        val pending = async { client.placeAsync(OrderDraft("SKU-41", 3)) }
        check(withContext(Dispatchers.IO) { replyEntered.await(5, TimeUnit.SECONDS) })
        client.requestClose()
        check(!abi.registrationReleased && abi.ackCount.get() == 0) {
            "revoke released the registration while a claimed native reply call was active"
        }
        replyGate.countDown()
        check(pending.await() == OrderReceipt(41, "SKU-41", 3))
        client.awaitClosed()
        check(abi.replyCount.get() == 1 && abi.ackCount.get() == 1 && abi.releaseCount.get() == 1)
        println("PASS Kotlin lifecycle v2 defers AckDrain until an in-native-call reply returns")
    }

    run {
        val abi = TypedReplyFake()
        lateinit var client: OrdersClient
        client = OrdersClient(
            abi,
            CoroutineScope(SupervisorJob() + Dispatchers.Default),
            {
                client.requestClose()
                true
            },
            { })
        val failure = withTimeout(5_000) {
            runCatching { client.placeAsync(OrderDraft("SKU-41", 3)) }.exceptionOrNull()
        }
        check((failure as? LiftAbiException)?.statusCode == 11)
        client.awaitClosed()
        check(abi.replyCount.get() == 0 && abi.ackCount.get() == 1 && abi.releaseCount.get() == 1)
        println("PASS Kotlin lifecycle v2 reentrant requestClose does not deadlock or reply")
    }

    run {
        val abi = TypedReplyFake()
        val client = OrdersClient(abi, CoroutineScope(SupervisorJob() + Dispatchers.Default), { true }, { })
        withTimeout(5_000) {
            kotlinx.coroutines.coroutineScope {
                repeat(16) {
                    launch(Dispatchers.Default) {
                        client.requestClose()
                        client.awaitClosed()
                    }
                }
            }
        }
        check(abi.revokeCount.get() == 1 && abi.ackCount.get() == 1 && abi.releaseCount.get() == 1)
        check(abi.ackObservedInsideOnRevoke) {
            "empty request set did not acknowledge reentrantly from OnRevoke"
        }
        println("PASS Kotlin lifecycle v2 concurrent close is idempotent")
    }

    check(runCatching {
        OrdersClient(
            TypedReplyFake(featureBits = NetLiftOrdersContract.FEATURE_ASYNC_CALLBACKS),
            CoroutineScope(SupervisorJob() + Dispatchers.Default),
            { true },
            { })
    }.exceptionOrNull() is LiftAbiException)
    check(runCatching {
        OrdersClient(
            TypedReplyFake(runtimeHash = ByteArray(32)),
            CoroutineScope(SupervisorJob() + Dispatchers.Default),
            { true },
            { })
    }.exceptionOrNull()?.let { it is LiftAbiException && it.statusCode == 5 } == true)
    check(runCatching {
        OrdersClient(
            TypedReplyFake(runtimeVersion = 2),
            CoroutineScope(SupervisorJob() + Dispatchers.Default),
            { true },
            { })
    }.exceptionOrNull()?.let { it is LiftAbiException && it.statusCode == 4 } == true)
    println("PASS Kotlin schema-v3 typed reply handshake, result/decline, malformed/unknown, cancellation, and handler failure")
}
