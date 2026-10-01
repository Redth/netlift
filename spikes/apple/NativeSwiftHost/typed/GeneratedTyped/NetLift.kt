// Generated schema projection; async runtime is limited to the supported Orders facade.
@file:OptIn(ExperimentalUnsignedTypes::class)
package com.netlift.androidhost.contract
import kotlinx.coroutines.launch

interface Orders {
    suspend fun placeAsync(draft: OrderDraft): OrderReceipt
}
interface Confirmation {
    suspend fun confirmAsync(prompt: String): Boolean
}
data class OrderDraft(val sku: String, val quantity: Int)
data class OrderReceipt(val id: Long, val sku: String, val quantity: Int)
private fun netLiftEncodeUtf8(value: String): ByteArray
{
    val encoder = java.nio.charset.StandardCharsets.UTF_8.newEncoder()
        .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
        .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
    val encoded = encoder.encode(java.nio.CharBuffer.wrap(value))
    if (encoded.remaining() > 4096) throw IllegalArgumentException("UTF-8 value exceeds the ABI buffer limit.")
    return ByteArray(encoded.remaining()).also { encoded.get(it) }
}

private fun netLiftDecodeUtf8(bytes: ByteArray): String
{
    if (bytes.size > 4096) throw IllegalArgumentException("UTF-8 value exceeds the ABI buffer limit.")
    val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
        .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
        .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
    return decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString()
}

public class LiftAbiException(val statusCode: Int, messageUtf8: ByteArray) :
    Exception(netLiftDecodeUtf8(messageUtf8.copyOf()))

public data class LiftNativeRuntimeInfo(
    val abiVersion: Int,
    val featureBits: Int,
    val schemaHash: ByteArray)

/**
 * JNI adapters must copy/free owned C output buffers and copy borrowed callback bytes
 * before returning. Only byte arrays cross JNI; strings are strict UTF-8 in Kotlin.
 */
public interface LiftNativeAbi
{
    fun start(abiVersion: Int, schemaHash: ByteArray): LiftNativeRuntimeInfo
    fun registerCallbacks(serviceId: Int, callbacks: LiftNativeCallbacks): Long
    fun revoke(registrationToken: Long)
    fun begin(serviceId: Int, methodId: Int, registrationToken: Long, arguments: ByteArray): Long
    fun activate(operationToken: Long)
    fun cancel(operationToken: Long)
    fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean)
}

public data class LiftNativeReply(val outcome: Int, val accepted: Boolean = false, val diagnostic: ByteArray = byteArrayOf())

public object LiftNativeReplyOutcome
{
    public const val RESULT = 0
    public const val CANCELLED = 1
    public const val UNAVAILABLE = 2
    public const val MALFORMED_PAYLOAD = 3
    public const val UNKNOWN_METHOD = 4
    public const val HANDLER_FAILURE = 5
}

public interface LiftNativeTypedReplyAbi : LiftNativeAbi
{
    fun replyTyped(registrationToken: Long, requestToken: Long, reply: LiftNativeReply)
}

public interface LiftNativeCallbacks
{
    fun onRequest(registrationToken: Long, requestToken: Long, methodId: Int, payload: ByteArray)
    fun onResult(operationToken: Long, status: Int, result: ByteArray, error: ByteArray)
    fun onRelease(registrationToken: Long)
}

public data class NativeConfirmationRequest(val prompt: String)

public object NetLiftOrdersContract
{
    public const val ABI_VERSION = 1
    public const val FEATURE_ASYNC_CALLBACKS = 2
    public const val FEATURE_TYPED_NATIVE_REPLY = 4
    public const val MAX_BUFFER_BYTES = 4096
    public const val ORDERS_SERVICE_ID = 2
    public const val PLACE_ORDER_METHOD_ID = 1
    public const val CONFIRMATION_SERVICE_ID = 3
    public const val CONFIRM_ORDER_METHOD_ID = 1
    public const val SCHEMA_HASH_HEX = "eeda280e446ad6a906222f1ca9fd458c80b2fd279507804449f3700322b36f05"
    public val schemaHash: ByteArray
        get() = byteArrayOf(0xee.toByte(), 0xda.toByte(), 0x28.toByte(), 0x0e.toByte(), 0x44.toByte(), 0x6a.toByte(), 0xd6.toByte(), 0xa9.toByte(), 0x06.toByte(), 0x22.toByte(), 0x2f.toByte(), 0x1c.toByte(), 0xa9.toByte(), 0xfd.toByte(), 0x45.toByte(), 0x8c.toByte(), 0x80.toByte(), 0xb2.toByte(), 0xfd.toByte(), 0x27.toByte(), 0x95.toByte(), 0x07.toByte(), 0x80.toByte(), 0x44.toByte(), 0x49.toByte(), 0xf3.toByte(), 0x70.toByte(), 0x03.toByte(), 0x22.toByte(), 0xb3.toByte(), 0x6f.toByte(), 0x05.toByte())
}

private object NetLiftOrdersWire
{
    private const val MAX_BYTES = NetLiftOrdersContract.MAX_BUFFER_BYTES

    private fun writeField(output: java.io.ByteArrayOutputStream, fieldId: Int, payload: ByteArray)
    {
        require(fieldId in 0..65535) { "Field ID is outside uint16." }
        require(payload.size <= MAX_BYTES - output.size() - 6) { "Encoded DTO exceeds the ABI buffer limit." }
        output.write(fieldId and 0xff)
        output.write((fieldId ushr 8) and 0xff)
        for (shift in 0..3) output.write((payload.size ushr (shift * 8)) and 0xff)
        output.write(payload)
    }

    private fun writeString(output: java.io.ByteArrayOutputStream, fieldId: Int, value: String) =
        writeField(output, fieldId, netLiftEncodeUtf8(value))

    private fun writeInt32(output: java.io.ByteArrayOutputStream, fieldId: Int, value: Int) =
        writeField(output, fieldId, byteArrayOf(
            value.toByte(), (value ushr 8).toByte(), (value ushr 16).toByte(), (value ushr 24).toByte()))

    private fun writeInt64(output: java.io.ByteArrayOutputStream, fieldId: Int, value: Long) =
        writeField(output, fieldId, ByteArray(8) { index -> (value ushr (index * 8)).toByte() })

    private class Reader(private val source: ByteArray)
    {
        private var offset = 0

        init { require(source.size <= MAX_BYTES) { "DTO exceeds the ABI buffer limit." } }

        fun field(expectedId: Int): ByteArray
        {
            require(offset >= 0 && source.size - offset >= 6) { "Truncated DTO field header." }
            val actualId = (source[offset].toInt() and 0xff) or
                ((source[offset + 1].toInt() and 0xff) shl 8)
            require(actualId == expectedId) { "Unexpected, duplicate, or reordered DTO field ID." }
            var length = 0L
            for (index in 0..3)
                length = length or ((source[offset + 2 + index].toLong() and 0xffL) shl (index * 8))
            offset += 6
            require(length <= MAX_BYTES && length <= (source.size - offset).toLong()) { "Invalid DTO field length." }
            val count = length.toInt()
            return source.copyOfRange(offset, offset + count).also { offset += count }
        }

        fun string(fieldId: Int): String = netLiftDecodeUtf8(field(fieldId))

        fun int32(fieldId: Int): Int
        {
            val value = field(fieldId)
            require(value.size == 4) { "Invalid int32 field width." }
            return (value[0].toInt() and 0xff) or
                ((value[1].toInt() and 0xff) shl 8) or
                ((value[2].toInt() and 0xff) shl 16) or
                ((value[3].toInt() and 0xff) shl 24)
        }

        fun int64(fieldId: Int): Long
        {
            val value = field(fieldId)
            require(value.size == 8) { "Invalid int64 field width." }
            var result = 0L
            value.forEachIndexed { index, byte ->
                result = result or ((byte.toLong() and 0xffL) shl (index * 8))
            }
            return result
        }

        fun requireEnd() { require(offset == source.size) { "Trailing DTO bytes." } }
    }

    fun encodeDraft(value: OrderDraft): ByteArray
    {
        val output = java.io.ByteArrayOutputStream()
        writeString(output, 1, value.sku)
        writeInt32(output, 2, value.quantity)
        return output.toByteArray()
    }

    fun decodeReceipt(source: ByteArray): OrderReceipt
    {
        val reader = Reader(source)
        val id = reader.int64(1)
        val sku = reader.string(2)
        val quantity = reader.int32(3)
        reader.requireEnd()
        return OrderReceipt(id, sku, quantity)
    }

    fun encodeConfirmation(request: NativeConfirmationRequest): ByteArray
    {
        val output = java.io.ByteArrayOutputStream()
        writeString(output, 1, request.prompt)
        return output.toByteArray()
    }

    fun decodeConfirmation(source: ByteArray): NativeConfirmationRequest
    {
        val reader = Reader(source)
        val prompt = reader.string(1)
        reader.requireEnd()
        return NativeConfirmationRequest(prompt)
    }
}

public class OrdersClient(
    private val abi: LiftNativeTypedReplyAbi,
    private val callbackScope: kotlinx.coroutines.CoroutineScope,
    private val confirmation: suspend (NativeConfirmationRequest) -> Boolean,
    private val onError: (Exception) -> Unit)
{
    private val gate = Any()
    private val pending = mutableMapOf<Long, kotlinx.coroutines.CancellableContinuation<OrderReceipt>>()
    private val requests = mutableMapOf<Long, RequestState>()
    private val releaseWaiters = mutableListOf<kotlinx.coroutines.CancellableContinuation<Unit>>()
    private var closing = false
    private var released = false
    private var revokeStarted = false
    private var diagnosticCount = 0
    private var suppressedDiagnosticCount = 0
    private var registrationToken = 0L
    private val callbacks = object : LiftNativeCallbacks
    {
        override fun onRequest(registrationToken: Long, requestToken: Long, methodId: Int, payload: ByteArray) =
            handleRequest(registrationToken, requestToken, methodId, payload.copyOf())
        override fun onResult(operationToken: Long, status: Int, result: ByteArray, error: ByteArray) =
            handleResult(operationToken, status, result.copyOf(), error.copyOf())
        override fun onRelease(registrationToken: Long) = handleRelease(registrationToken)
    }

    private class RequestState(var job: kotlinx.coroutines.Job? = null)

    public val suppressedDiagnostics: Int
        get() = synchronized(gate) { suppressedDiagnosticCount }

    init
    {
        val expectedHash = NetLiftOrdersContract.schemaHash
        val runtime = abi.start(NetLiftOrdersContract.ABI_VERSION, expectedHash.copyOf())
        if (runtime.abiVersion != NetLiftOrdersContract.ABI_VERSION)
            throw LiftAbiException(4, netLiftEncodeUtf8("ABI version mismatch."))
        if (!runtime.schemaHash.contentEquals(expectedHash))
            throw LiftAbiException(5, netLiftEncodeUtf8("Contract schema hash mismatch."))
        if (runtime.featureBits and NetLiftOrdersContract.FEATURE_ASYNC_CALLBACKS == 0)
            throw LiftAbiException(3, netLiftEncodeUtf8("Async callback ABI feature is unavailable."))
        if (runtime.featureBits and NetLiftOrdersContract.FEATURE_TYPED_NATIVE_REPLY == 0)
            throw LiftAbiException(3, netLiftEncodeUtf8("Typed native reply ABI feature is unavailable."))
        registrationToken = abi.registerCallbacks(NetLiftOrdersContract.CONFIRMATION_SERVICE_ID, callbacks)
        if (registrationToken == 0L)
            throw LiftAbiException(1, netLiftEncodeUtf8("Native callback registration returned an empty token."))
    }

    public suspend fun placeAsync(draft: OrderDraft): OrderReceipt =
        kotlinx.coroutines.suspendCancellableCoroutine { continuation ->
            var operationToken = 0L
            var inserted = false
            try
            {
                synchronized(gate) {
                    if (closing || released) throw LiftAbiException(11, netLiftEncodeUtf8("Orders client is closed."))
                }
                val arguments = NetLiftOrdersWire.encodeDraft(draft)
                operationToken = abi.begin(
                    NetLiftOrdersContract.ORDERS_SERVICE_ID,
                    NetLiftOrdersContract.PLACE_ORDER_METHOD_ID,
                    registrationToken,
                    arguments)
                if (operationToken == 0L)
                    throw LiftAbiException(1, netLiftEncodeUtf8("Begin returned an empty operation token."))
                synchronized(gate) {
                    if (closing || released) throw LiftAbiException(11, netLiftEncodeUtf8("Orders client is closed."))
                    check(pending.put(operationToken, continuation) == null) { "Duplicate operation token." }
                }
                inserted = true
                continuation.invokeOnCancellation { cancelPending(operationToken) }
                if (!continuation.isActive) return@suspendCancellableCoroutine
                abi.activate(operationToken)
            }
            catch (error: Exception)
            {
                if (inserted)
                {
                    val owned = synchronized(gate) { pending.remove(operationToken) }
                    if (owned != null)
                    {
                        cancelNative(operationToken)
                        owned.resumeWith(Result.failure(error))
                    }
                }
                else
                {
                    if (operationToken != 0L) cancelNative(operationToken)
                    continuation.resumeWith(Result.failure(error))
                }
            }
        }

    public suspend fun close()
    {
        val revokeToken = synchronized(gate) {
            closing = true
            if (released) return
            if (revokeStarted) 0L else {
                revokeStarted = true
                registrationToken
            }
        }
        if (revokeToken != 0L)
        {
            try { abi.revoke(revokeToken) }
            catch (error: Exception)
            {
                synchronized(gate) { if (!released) revokeStarted = false }
                throw error
            }
        }
        kotlinx.coroutines.suspendCancellableCoroutine<Unit> { continuation ->
            val resumeNow = synchronized(gate) {
                if (released) true
                else {
                    releaseWaiters.add(continuation)
                    false
                }
            }
            continuation.invokeOnCancellation {
                synchronized(gate) { releaseWaiters.remove(continuation) }
            }
            if (resumeNow) continuation.resumeWith(Result.success(Unit))
        }
    }

    private fun cancelPending(token: Long)
    {
        val owned = synchronized(gate) { pending.remove(token) }
        if (owned != null) cancelNative(token)
    }

    private fun cancelNative(token: Long)
    {
        try { abi.cancel(token) }
        catch (error: Exception) { report(error) }
    }

    private fun handleResult(token: Long, status: Int, result: ByteArray, error: ByteArray)
    {
        val continuation = synchronized(gate) { pending.remove(token) }
        if (continuation == null)
        {
            report(IllegalStateException("Late or duplicate native result for operation token $token."))
            return
        }
        try
        {
            when
            {
                status == 12 -> continuation.resumeWith(
                    Result.failure(kotlinx.coroutines.CancellationException("Managed operation was cancelled.")))
                status != 0 -> continuation.resumeWith(Result.failure(LiftAbiException(status, error)))
                else -> continuation.resumeWith(Result.success(NetLiftOrdersWire.decodeReceipt(result)))
            }
        }
        catch (failure: Exception)
        {
            report(failure)
            continuation.resumeWith(Result.failure(failure))
        }
    }

    private fun handleRequest(registration: Long, token: Long, methodId: Int, payload: ByteArray)
    {
        val state = RequestState()
        val admitted = synchronized(gate) {
            if (registration != registrationToken || released || requests.containsKey(token)) false
            else {
                requests[token] = state
                true
            }
        }
        if (!admitted)
        {
            report(IllegalStateException("Unexpected, duplicate, or stale native request token $token."))
            return
        }
        if (methodId != NetLiftOrdersContract.CONFIRM_ORDER_METHOD_ID)
        {
            report(IllegalStateException("Unknown native confirmation method ID $methodId."))
            finishRequest(registration, token, state, LiftNativeReply(
                LiftNativeReplyOutcome.UNKNOWN_METHOD,
                diagnostic = netLiftEncodeUtf8("Unknown native confirmation method.")))
            return
        }
        val request = try { NetLiftOrdersWire.decodeConfirmation(payload) }
        catch (failure: Exception)
        {
            report(failure)
            finishRequest(registration, token, state, LiftNativeReply(
                LiftNativeReplyOutcome.MALFORMED_PAYLOAD,
                diagnostic = netLiftEncodeUtf8("Malformed native confirmation payload.")))
            return
        }
        val job = callbackScope.launch(start = kotlinx.coroutines.CoroutineStart.LAZY) {
            val reply = try {
                LiftNativeReply(LiftNativeReplyOutcome.RESULT, confirmation(request))
            }
            catch (cancelled: kotlinx.coroutines.CancellationException)
            {
                report(IllegalStateException("Native confirmation handler was cancelled.", cancelled))
                LiftNativeReply(LiftNativeReplyOutcome.CANCELLED)
            }
            catch (failure: Exception)
            {
                report(failure)
                LiftNativeReply(
                    LiftNativeReplyOutcome.HANDLER_FAILURE,
                    diagnostic = boundedDiagnostic(failure))
            }
            finishRequest(registration, token, state, reply)
        }
        val startJob = synchronized(gate) {
            if (released || requests[token] !== state) false
            else {
                state.job = job
                true
            }
        }
        if (startJob)
        {
            job.invokeOnCompletion { cause ->
                if (cause is kotlinx.coroutines.CancellationException)
                {
                    val stillPending = synchronized(gate) { !released && requests[token] === state }
                    if (stillPending)
                    {
                        report(IllegalStateException("Confirmation callback scope was cancelled.", cause))
                        finishRequest(registration, token, state, LiftNativeReply(
                            LiftNativeReplyOutcome.CANCELLED))
                    }
                }
            }
            job.start()
        }
        else job.cancel()
    }

    private fun finishRequest(
        registration: Long,
        token: Long,
        state: RequestState,
        reply: LiftNativeReply)
    {
        val shouldReply = synchronized(gate) {
            if (released || requests[token] !== state) false
            else {
                requests.remove(token)
                true
            }
        }
        if (!shouldReply)
        {
            report(IllegalStateException("Late native confirmation reply for request token $token."))
            return
        }
        try { abi.replyTyped(registration, token, reply) }
        catch (failure: Exception) { report(failure) }
    }

    private fun boundedDiagnostic(error: Exception): ByteArray
    {
        val fallback = "Native handler failed; diagnostic unavailable or too large."
            .toByteArray(Charsets.US_ASCII)
        val bytes = try {
            netLiftEncodeUtf8(error.message ?: error.javaClass.simpleName)
        }
        catch (_: Exception) { fallback }
        return if (bytes.size <= NetLiftOrdersContract.MAX_BUFFER_BYTES) bytes else fallback
    }
    private fun handleRelease(token: Long)
    {
        val release = synchronized(gate) {
            if (token != registrationToken || released) null
            else {
                released = true
                closing = true
                val waiters = releaseWaiters.toList()
                releaseWaiters.clear()
                val operations = pending.values.toList()
                pending.clear()
                val jobs = requests.values.mapNotNull { it.job }
                requests.clear()
                Triple(waiters, operations, jobs)
            }
        }
        if (release == null)
        {
            report(IllegalStateException("Unexpected or duplicate native release token $token."))
            return
        }
        release.third.forEach { it.cancel() }
        release.second.forEach {
            it.resumeWith(Result.failure(LiftAbiException(11, netLiftEncodeUtf8("Native registration was released."))))
        }
        release.first.forEach { it.resumeWith(Result.success(Unit)) }
    }

    private fun report(error: Exception)
    {
        val shouldReport = synchronized(gate) {
            if (diagnosticCount < 16) {
                diagnosticCount++
                true
            } else {
                suppressedDiagnosticCount++
                false
            }
        }
        if (shouldReport)
        {
            try { onError(error) }
            catch (sinkFailure: Exception)
            {
                System.err.println("NetLift diagnostic sink failed: ${sinkFailure.message}; original: ${error.message}")
            }
        }
    }
}
