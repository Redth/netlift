// Generated schema projection; async runtime is limited to the supported Orders facade.
@file:OptIn(ExperimentalUnsignedTypes::class)
@file:JvmName("NetLiftStreamV4")
package com.netlift.androidhost.contract
import kotlinx.coroutines.launch

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withContext

interface OrdersUpdates {
}
data class StreamOrderUpdate(val id: Long, val state: String)
public data class LiftStreamRuntimeInfo(val abiVersion: Int, val featureBits: Int, val schemaHash: ByteArray)

public interface LiftStreamCallbacks
{
    fun onNext(token: Long, borrowedItem: ByteArray)
    fun onTerminal(token: Long, status: Int, diagnostic: ByteArray)
    fun onRelease(token: Long)
}

public interface LiftNativeStreamAbi
{
    fun start(abiVersion: Int, schemaHash: ByteArray): LiftStreamRuntimeInfo
    fun subscribe(
        serviceId: Int,
        subscriptionId: Int,
        callbacks: LiftStreamCallbacks,
        initialCredit: Int,
        transportCapacity: Int,
        maxItemBytes: Int): Long
    fun activate(token: Long)
    fun request(token: Long, credits: Int)
    fun unsubscribe(token: Long)
}

private sealed interface LiftStreamMessage
{
    data class Item(val token: Long, val bytes: ByteArray) : LiftStreamMessage
    data class Terminal(val token: Long, val status: Int, val diagnostic: ByteArray) : LiftStreamMessage
}

public class LiftStreamException(val status: Int, message: String) : Exception(message)

private const val LIFT_STREAM_FEATURE = 8
private const val LIFT_STREAM_CAPACITY = 64
private const val LIFT_STREAM_MAX_ITEM_BYTES = 4096
private fun liftDecodeStreamDiagnostic(bytes: ByteArray): String
{
    if (bytes.size > LIFT_STREAM_MAX_ITEM_BYTES)
        throw IllegalArgumentException("Stream diagnostic exceeds 4096 bytes.")
    val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
        .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
        .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
    return decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString()
}

private fun <T> liftColdStream(
    abi: LiftNativeStreamAbi,
    serviceId: Int,
    subscriptionId: Int,
    schemaHash: ByteArray,
    decode: (ByteArray) -> T): kotlinx.coroutines.flow.Flow<T> = flow {
    val info = abi.start(1, schemaHash.copyOf())
    if (info.abiVersion != 1 || info.featureBits and LIFT_STREAM_FEATURE == 0 ||
        !info.schemaHash.contentEquals(schemaHash))
        throw LiftStreamException(4, "Stream ABI version, feature, or schema hash mismatch.")

    val messages = Channel<LiftStreamMessage>(2)
    val released = CompletableDeferred<Unit>()
    var token = 0L
    val callbacks = object : LiftStreamCallbacks
    {
        override fun onNext(token: Long, borrowedItem: ByteArray)
        {
            if (borrowedItem.size > LIFT_STREAM_MAX_ITEM_BYTES)
            {
                messages.trySend(LiftStreamMessage.Terminal(
                    token, 6, "Stream item exceeds 4096 bytes.".toByteArray(Charsets.UTF_8)))
                return
            }
            messages.trySend(LiftStreamMessage.Item(token, borrowedItem.copyOf()))
        }

        override fun onTerminal(token: Long, status: Int, diagnostic: ByteArray)
        {
            messages.trySend(LiftStreamMessage.Terminal(token, status, diagnostic.copyOf()))
        }

        override fun onRelease(token: Long) { released.complete(Unit) }
    }

    try
    {
        token = abi.subscribe(serviceId, subscriptionId, callbacks, 0,
            LIFT_STREAM_CAPACITY, LIFT_STREAM_MAX_ITEM_BYTES)
        require(token != 0L) { "Stream Subscribe returned an invalid token." }
        currentCoroutineContext().ensureActive()
        abi.activate(token)
        abi.request(token, 1)
        while (true)
        {
            when (val message = messages.receive())
            {
                is LiftStreamMessage.Item ->
                {
                    if (message.token == token)
                    {
                        emit(decode(message.bytes))
                        abi.request(token, 1)
                    }
                }
                is LiftStreamMessage.Terminal ->
                {
                    if (message.token != token) continue
                    when (message.status)
                    {
                        0 -> return@flow
                        1 -> throw kotlinx.coroutines.CancellationException("Native stream cancelled.")
                        else -> throw LiftStreamException(message.status,
                            liftDecodeStreamDiagnostic(message.diagnostic))
                    }
                }
            }
        }
    }
    finally
    {
        if (token != 0L)
        {
            abi.unsubscribe(token)
            withContext(NonCancellable) { released.await() }
        }
        messages.close()
    }
}
private val NETLIFT_STREAM_SCHEMA_HASH = byteArrayOf(0xe5.toByte(), 0x73.toByte(), 0x3c.toByte(), 0xe3.toByte(), 0x6f.toByte(), 0x1f.toByte(), 0xfc.toByte(), 0xc1.toByte(), 0x5a.toByte(), 0x23.toByte(), 0x9e.toByte(), 0x62.toByte(), 0x10.toByte(), 0xe1.toByte(), 0x00.toByte(), 0x68.toByte(), 0x63.toByte(), 0x01.toByte(), 0xfa.toByte(), 0x90.toByte(), 0x50.toByte(), 0xdd.toByte(), 0x6f.toByte(), 0x00.toByte(), 0x21.toByte(), 0x4b.toByte(), 0x42.toByte(), 0x8a.toByte(), 0x65.toByte(), 0xaa.toByte(), 0xaa.toByte(), 0x6d.toByte())
public object NetLiftStreamContract
{
    public const val ABI_VERSION = 1
    public const val FEATURE_STREAMS = 8
    public const val TRANSPORT_CAPACITY = 64
    public const val MAX_ITEM_BYTES = 4096
    public const val SCHEMA_HASH_HEX = "e5733ce36f1ffcc15a239e6210e100686301fa9050dd6f00214b428a65aaaa6d"
    public val schemaHash: ByteArray get() = NETLIFT_STREAM_SCHEMA_HASH.copyOf()
}
private class NetLiftStreamDecodeException(message: String) : IllegalArgumentException(message)

private object NetLiftStreamWire
{
    private const val MAX_BYTES = 4096

    private class Reader(private val source: ByteArray)
    {
        private var offset = 0

        init
        {
            if (source.size > MAX_BYTES)
                throw NetLiftStreamDecodeException("Stream item exceeds 4096 bytes.")
        }

        fun field(expectedId: Int): ByteArray?
        {
            if (offset < 0 || source.size - offset < 6)
                throw NetLiftStreamDecodeException("Truncated stream field header.")
            val actualId = (source[offset].toInt() and 0xff) or
                ((source[offset + 1].toInt() and 0xff) shl 8)
            if (actualId != expectedId)
                throw NetLiftStreamDecodeException("Unexpected, duplicate, or reordered stream field ID.")
            var length = 0L
            for (index in 0..3)
                length = length or ((source[offset + 2 + index].toLong() and 0xffL) shl (index * 8))
            offset += 6
            if (length == 0xffffffffL) return null
            if (length > MAX_BYTES || length > (source.size - offset).toLong())
                throw NetLiftStreamDecodeException("Invalid stream field length.")
            val count = length.toInt()
            return source.copyOfRange(offset, offset + count).also { offset += count }
        }

        fun string(fieldId: Int, nullable: Boolean): String?
        {
            val bytes = field(fieldId) ?: if (nullable) return null else
                throw NetLiftStreamDecodeException("Null in non-null stream field.")
            val decoder = java.nio.charset.StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
            return try { decoder.decode(java.nio.ByteBuffer.wrap(bytes)).toString() }
            catch (failure: java.nio.charset.CharacterCodingException)
            {
                throw NetLiftStreamDecodeException("Malformed UTF-8 stream field.")
            }
        }

        fun integer(fieldId: Int, width: Int, nullable: Boolean): ULong?
        {
            val bytes = field(fieldId) ?: if (nullable) return null else
                throw NetLiftStreamDecodeException("Null in non-null stream field.")
            if (bytes.size != width)
                throw NetLiftStreamDecodeException("Invalid fixed-width stream field.")
            var value = 0uL
            bytes.forEachIndexed { index, byte ->
                value = value or ((byte.toUByte().toULong()) shl (index * 8))
            }
            return value
        }

        fun boolean(fieldId: Int, nullable: Boolean): Boolean?
        {
            val bits = integer(fieldId, 1, nullable) ?: return null
            if (bits > 1uL)
                throw NetLiftStreamDecodeException("Invalid boolean stream field.")
            return bits == 1uL
        }

        fun requireEnd()
        {
            if (offset != source.size)
                throw NetLiftStreamDecodeException("Trailing stream item bytes.")
        }
    }
    fun decodeDto_43(source: ByteArray): StreamOrderUpdate
    {
        val reader = Reader(source)
        val bits_1 = reader.integer(1, 8, false) ?: throw NetLiftStreamDecodeException("Null in non-null stream field.")
        val value_1: Long = bits_1.toLong()
        val value_2: String = reader.string(2, false) ?: throw NetLiftStreamDecodeException("Null in non-null stream field.")
        reader.requireEnd()
        return StreamOrderUpdate(id = value_1, state = value_2)
    }
}
public fun watchStream(abi: LiftNativeStreamAbi, decode: (ByteArray) -> StreamOrderUpdate): Flow<StreamOrderUpdate> =
    liftColdStream(abi, 42, 1, NetLiftStreamContract.schemaHash, decode)
public fun watchStream(abi: LiftNativeStreamAbi): Flow<StreamOrderUpdate> =
    watchStream(abi, NetLiftStreamWire::decodeDto_43)
