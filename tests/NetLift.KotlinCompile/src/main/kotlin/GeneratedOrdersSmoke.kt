package com.netlift.androidhost.contract

import java.io.ByteArrayOutputStream
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout

private enum class FakeMode { ACCEPT, MANAGED_ERROR, MALFORMED_CONFIRMATION, DUPLICATE_RESULT }

private class FakeLiftNativeAbi(
    private val mode: FakeMode = FakeMode.ACCEPT,
    private val runtimeHash: ByteArray? = null,
    private val runtimeVersion: Int = NetLiftOrdersContract.ABI_VERSION
) : LiftNativeAbi
{
    val events = CopyOnWriteArrayList<String>()
    val errors = CopyOnWriteArrayList<Exception>()
    val requestEntered = CountDownLatch(1)
    val duplicateResultDelivered = CountDownLatch(1)
    val nativeCancelCount = AtomicInteger()
    val nativeReplyCount = AtomicInteger()
    val registrationCount = AtomicInteger()
    val resultCount = AtomicInteger()
    val cancelled = AtomicBoolean()
    @Volatile var requestedVersion = 0
    @Volatile var requestedHash = byteArrayOf()
    @Volatile private var callbacks: LiftNativeCallbacks? = null
    @Volatile private var replyAccepted = false
    @Volatile private var replyLatch = CountDownLatch(1)
    @Volatile private var operationToken = 0L
    private val registrationToken = 0x0000000100000003L
    private val requestToken = 0x0000000100000007L

    override fun start(abiVersion: Int, schemaHash: ByteArray): LiftNativeRuntimeInfo
    {
        events += "start"
        requestedVersion = abiVersion
        requestedHash = schemaHash.copyOf()
        return LiftNativeRuntimeInfo(
            runtimeVersion,
            NetLiftOrdersContract.FEATURE_ASYNC_CALLBACKS,
            runtimeHash?.copyOf() ?: schemaHash.copyOf())
    }

    override fun registerCallbacks(serviceId: Int, callbacks: LiftNativeCallbacks): Long
    {
        check(serviceId == NetLiftOrdersContract.CONFIRMATION_SERVICE_ID)
        events += "register"
        registrationCount.incrementAndGet()
        this.callbacks = object : LiftNativeCallbacks
        {
            override fun onRequest(registrationToken: Long, requestToken: Long, methodId: Int, payload: ByteArray)
            {
                events += "request"
                callbacks.onRequest(registrationToken, requestToken, methodId, payload)
            }

            override fun onResult(operationToken: Long, status: Int, result: ByteArray, error: ByteArray)
            {
                events += "result"
                resultCount.incrementAndGet()
                callbacks.onResult(operationToken, status, result, error)
            }

            override fun onRelease(registrationToken: Long)
            {
                events += "release"
                callbacks.onRelease(registrationToken)
            }
        }
        return registrationToken
    }

    override fun begin(serviceId: Int, methodId: Int, registrationToken: Long, arguments: ByteArray): Long
    {
        check(serviceId == NetLiftOrdersContract.ORDERS_SERVICE_ID)
        check(methodId == NetLiftOrdersContract.PLACE_ORDER_METHOD_ID)
        check(registrationToken == this.registrationToken)
        check(arguments.contentEquals(orderDraftBytes("SKU-41", 3))) {
            "generated request codec bytes differed: ${arguments.joinToString { "%02x".format(it) }}"
        }
        events += "begin"
        operationToken = 0x000000020000000bL
        return operationToken
    }

    override fun activate(operationToken: Long)
    {
        check(operationToken == this.operationToken)
        events += "activate"
        val requestPayload = if (mode == FakeMode.MALFORMED_CONFIRMATION)
            byteArrayOf(1, 0, 1, 0, 0, 0, 0xff.toByte())
        else
            confirmationBytes("Confirm order SKU-41 x 3")
        callbacks!!.onRequest(
            registrationToken,
            requestToken,
            NetLiftOrdersContract.CONFIRM_ORDER_METHOD_ID,
            requestPayload)
        requestEntered.countDown()
        Thread {
            if (!replyLatch.await(5, TimeUnit.SECONDS) || cancelled.get()) return@Thread
            val target = callbacks ?: return@Thread
            if (mode == FakeMode.MANAGED_ERROR || !replyAccepted)
            {
                target.onResult(
                    operationToken,
                    8,
                    byteArrayOf(),
                    "Managed order operation failed.".toByteArray(Charsets.UTF_8))
            }
            else
            {
                val receipt = orderReceiptBytes(41, "SKU-41", 3)
                target.onResult(operationToken, 0, receipt, byteArrayOf())
                if (mode == FakeMode.DUPLICATE_RESULT)
                {
                    target.onResult(operationToken, 0, receipt, byteArrayOf())
                    duplicateResultDelivered.countDown()
                }
            }
        }.start()
    }

    override fun cancel(operationToken: Long)
    {
        check(operationToken == this.operationToken)
        events += "cancel"
        nativeCancelCount.incrementAndGet()
        cancelled.set(true)
        callbacks!!.onResult(operationToken, 12, byteArrayOf(), byteArrayOf())
    }

    override fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean)
    {
        check(registrationToken == this.registrationToken)
        check(requestToken == this.requestToken)
        events += "reply"
        nativeReplyCount.incrementAndGet()
        if (cancelled.get())
            throw LiftAbiException(10, "Native request token is stale.".toByteArray(Charsets.UTF_8))
        replyAccepted = accepted
        replyLatch.countDown()
    }

    override fun revoke(registrationToken: Long)
    {
        check(registrationToken == this.registrationToken)
        events += "revoke"
        callbacks!!.onRelease(registrationToken)
    }

    fun expectedSchemaHash(): ByteArray =
        NetLiftOrdersContract.SCHEMA_HASH_HEX.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
}

private fun client(
    abi: FakeLiftNativeAbi,
    scope: CoroutineScope,
    errors: MutableList<Exception> = abi.errors,
    confirmation: suspend (NativeConfirmationRequest) -> Boolean
) = OrdersClient(abi, scope, confirmation, errors::add)

fun main() = runBlocking {
    check(NetLiftOrdersContract.ORDERS_SERVICE_ID == 2)
    check(NetLiftOrdersContract.PLACE_ORDER_METHOD_ID == 1)
    check(NetLiftOrdersContract.CONFIRMATION_SERVICE_ID == 3)
    check(NetLiftOrdersContract.CONFIRM_ORDER_METHOD_ID == 1)
    check(NetLiftOrdersContract.SCHEMA_HASH_HEX ==
        "c4dd2a99283cfc9e1012554d95777b0b11a713f812b997021e197c129684e2f4")
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    val acceptAbi = FakeLiftNativeAbi()
    val acceptClient = client(acceptAbi, scope) { request ->
        check(request == NativeConfirmationRequest("Confirm order SKU-41 x 3"))
        true
    }
    check(acceptAbi.requestedVersion == NetLiftOrdersContract.ABI_VERSION)
    check(acceptAbi.requestedHash.contentEquals(acceptAbi.expectedSchemaHash()))
    check(acceptAbi.requestedHash.size == 32)
    check(acceptAbi.events.take(2) == listOf("start", "register"))
    check(acceptClient.placeAsync(OrderDraft("SKU-41", 3)) == OrderReceipt(41, "SKU-41", 3))
    withContext(Dispatchers.IO) {
        check(acceptAbi.requestEntered.await(5, TimeUnit.SECONDS))
    }
    check(acceptAbi.nativeReplyCount.get() == 1)
    acceptClient.close()
    check(acceptAbi.events.indexOf("reply") < acceptAbi.events.indexOf("revoke"))
    check(acceptAbi.events.takeLast(2) == listOf("revoke", "release"))
    println("PASS Kotlin Orders ABI-v1/schema-v2 handshake and generated wire codec")

    val errorAbi = FakeLiftNativeAbi(FakeMode.MANAGED_ERROR)
    val errorClient = client(errorAbi, scope) { true }
    try
    {
        errorClient.placeAsync(OrderDraft("SKU-41", 3))
        error("managed error was returned as a successful OrderReceipt")
    }
    catch (failure: LiftAbiException)
    {
        check(failure.statusCode == 8)
        check(failure.message == "Managed order operation failed.")
    }
    errorClient.close()
    println("PASS Kotlin Orders managed error propagation")

    val malformedAbi = FakeLiftNativeAbi(FakeMode.MALFORMED_CONFIRMATION)
    val malformedClient = client(malformedAbi, scope) { error("malformed UTF-8 reached confirmation handler") }
    try
    {
        malformedClient.placeAsync(OrderDraft("SKU-41", 3))
        error("malformed confirmation did not fail the managed route")
    }
    catch (failure: LiftAbiException)
    {
        check(failure.statusCode == 8)
    }
    check(malformedAbi.errors.any { it is java.nio.charset.CharacterCodingException })
    check(malformedAbi.nativeReplyCount.get() == 1)
    malformedClient.close()
    println("PASS Kotlin strict UTF-8 callback rejection")

    val callbackErrorAbi = FakeLiftNativeAbi()
    val callbackErrorClient = client(callbackErrorAbi, scope) {
        throw IllegalStateException("Confirmation handler failed.")
    }
    try
    {
        callbackErrorClient.placeAsync(OrderDraft("SKU-41", 3))
        error("confirmation callback failure was returned as a successful receipt")
    }
    catch (failure: LiftAbiException)
    {
        check(failure.statusCode == 8)
    }
    check(callbackErrorAbi.errors.any { it.message == "Confirmation handler failed." })
    callbackErrorClient.close()
    println("PASS Kotlin confirmation-handler failure is reported and terminates request")

    val duplicateAbi = FakeLiftNativeAbi(FakeMode.DUPLICATE_RESULT)
    val duplicateClient = client(duplicateAbi, scope) { true }
    check(duplicateClient.placeAsync(OrderDraft("SKU-41", 3)) == OrderReceipt(41, "SKU-41", 3))
    withContext(Dispatchers.IO) {
        check(duplicateAbi.duplicateResultDelivered.await(5, TimeUnit.SECONDS))
    }
    check(duplicateAbi.resultCount.get() == 2)
    duplicateClient.close()
    check(duplicateAbi.errors.any { it.message?.contains("Late or duplicate native result") == true })
    println("PASS Kotlin duplicate/late result is ignored after one terminal completion")

    val cancelAbi = FakeLiftNativeAbi()
    val confirmationGate = CompletableDeferred<Boolean>()
    val cancelClient = client(cancelAbi, scope) { confirmationGate.await() }
    val call = async { cancelClient.placeAsync(OrderDraft("SKU-41", 3)) }
    withContext(Dispatchers.IO) {
        check(cancelAbi.requestEntered.await(5, TimeUnit.SECONDS))
    }
    call.cancel()
    try { call.await(); error("cancelled order returned a receipt") }
    catch (_: CancellationException) { }
    check(cancelAbi.nativeCancelCount.get() == 1)
    confirmationGate.complete(true)
    withTimeout(5_000) {
        while (cancelAbi.errors.none { it is LiftAbiException && it.statusCode == 10 })
            kotlinx.coroutines.delay(5)
    }
    check(cancelAbi.resultCount.get() == 1)
    cancelClient.close()
    println("PASS Kotlin cancellation, synchronous completion, and late confirmation reply")

    val callbackJob = SupervisorJob()
    val callbackScope = CoroutineScope(callbackJob + Dispatchers.Default)
    val orderScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    val callbackScopeAbi = FakeLiftNativeAbi()
    val callbackGate = CompletableDeferred<Boolean>()
    val callbackScopeClient = client(callbackScopeAbi, callbackScope) { callbackGate.await() }
    val callbackScopeCall = orderScope.async { callbackScopeClient.placeAsync(OrderDraft("SKU-41", 3)) }
    withContext(Dispatchers.IO) {
        check(callbackScopeAbi.requestEntered.await(5, TimeUnit.SECONDS))
    }
    callbackJob.cancel()
    try
    {
        callbackScopeCall.await()
        error("cancelled confirmation scope returned a successful receipt")
    }
    catch (failure: LiftAbiException)
    {
        check(failure.statusCode == 8)
    }
    check(callbackScopeAbi.nativeReplyCount.get() == 1)
    check(callbackScopeAbi.errors.any { it.message?.contains("cancelled") == true })
    callbackScopeClient.close()
    orderScope.coroutineContext[kotlinx.coroutines.Job]?.cancel()
    println("PASS Kotlin confirmation-scope cancellation terminates native request")

    val wrongHash = acceptAbi.expectedSchemaHash().also { it[0] = (it[0].toInt() xor 0xff).toByte() }
    val mismatchedAbi = FakeLiftNativeAbi(runtimeHash = wrongHash)
    try
    {
        client(mismatchedAbi, scope) { true }
        error("client accepted a mismatched schema hash")
    }
    catch (failure: LiftAbiException)
    {
        check(failure.statusCode == 5)
    }
    check(mismatchedAbi.registrationCount.get() == 0)

    val wrongVersionAbi = FakeLiftNativeAbi(runtimeVersion = NetLiftOrdersContract.ABI_VERSION + 1)
    try
    {
        client(wrongVersionAbi, scope) { true }
        error("client accepted a mismatched ABI version")
    }
    catch (failure: LiftAbiException)
    {
        check(failure.statusCode == 4)
    }
    check(wrongVersionAbi.registrationCount.get() == 0)

    val abiMethods = LiftNativeAbi::class.java.methods
    check(abiMethods.none { method -> method.parameterTypes.any { it == String::class.java } })
    val callbackMethods = LiftNativeCallbacks::class.java.methods
    check(callbackMethods.none { method -> method.parameterTypes.any { it == String::class.java } })
    scope.coroutineContext[kotlinx.coroutines.Job]?.cancel()
    println("PASS Kotlin exact hash/version gates and ByteArray-only JNI signatures")
    runKotlinStreamSmoke()
}

private fun confirmationBytes(value: String): ByteArray =
    field(1, value.toByteArray(Charsets.UTF_8))

private fun orderDraftBytes(sku: String, quantity: Int): ByteArray =
    field(1, sku.toByteArray(Charsets.UTF_8)) + field(2, int32(quantity))

private fun orderReceiptBytes(id: Long, sku: String, quantity: Int): ByteArray =
    field(1, int64(id)) + field(2, sku.toByteArray(Charsets.UTF_8)) + field(3, int32(quantity))

private fun field(id: Int, bytes: ByteArray): ByteArray =
    ByteArrayOutputStream().apply {
        write(id and 0xff)
        write((id ushr 8) and 0xff)
        repeat(4) { shift -> write((bytes.size ushr (shift * 8)) and 0xff) }
        write(bytes)
    }.toByteArray()

private fun int32(value: Int): ByteArray =
    ByteArray(4) { index -> (value ushr (index * 8)).toByte() }

private fun int64(value: Long): ByteArray =
    ByteArray(8) { index -> (value ushr (index * 8)).toByte() }
