package com.netlift.androidhost.contract

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking

private class FakeLiftNativeStreamAbi(
    private val wrongHash: Boolean = false,
    private val failActivation: Boolean = false,
    private val failSubscribe: Boolean = false,
    private val wrongVersion: Boolean = false,
    private val expectedSubscriptionId: Int = 4,
    private val itemPayloads: List<ByteArray> = listOf(pulseBytes(7), pulseBytes(8))
) : LiftNativeStreamAbi
{
    val events = CopyOnWriteArrayList<String>()
    var onSubscribed: (() -> Unit)? = null
    private var callbacks: LiftStreamCallbacks? = null
    private var token = 0x0000000100000007L
    private var requests = 0
    var expectedHash = byteArrayOf()
    var unsubscribeCount = 0
        private set
    var releaseCount = 0
        private set

    override fun start(abiVersion: Int, schemaHash: ByteArray): LiftStreamRuntimeInfo
    {
        events += "start"
        expectedHash = schemaHash.copyOf()
        return LiftStreamRuntimeInfo(
            if (wrongVersion) abiVersion + 1 else abiVersion,
            NetLiftStreamContract.FEATURE_STREAMS,
            if (wrongHash) schemaHash.copyOf().also { it[0] = (it[0].toInt() xor 1).toByte() } else schemaHash.copyOf())
    }

    override fun subscribe(
        serviceId: Int,
        subscriptionId: Int,
        callbacks: LiftStreamCallbacks,
        initialCredit: Int,
        transportCapacity: Int,
        maxItemBytes: Int
    ): Long
    {
        check(serviceId == 20 && subscriptionId == expectedSubscriptionId)
        check(initialCredit == 0 && transportCapacity == 64 && maxItemBytes == 4096)
        events += "subscribe"
        if (failSubscribe) throw LiftStreamException(1, "subscribe failed")
        this.callbacks = callbacks
        onSubscribed?.invoke()
        return token
    }

    override fun activate(token: Long)
    {
        check(token == this.token)
        events += "activate"
        if (failActivation) throw LiftStreamException(1, "activation failed")
    }

    override fun request(token: Long, credits: Int)
    {
        check(token == this.token && credits == 1)
        requests += credits
        events += "request"
        if (requests <= itemPayloads.size)
            callbacks!!.onNext(token, itemPayloads[requests - 1])
        else
            callbacks!!.onTerminal(token, 0, byteArrayOf())
    }

    override fun unsubscribe(token: Long)
    {
        check(token == this.token)
        unsubscribeCount++
        events += "unsubscribe"
        callbacks!!.onRelease(token)
        releaseCount++
        events += "release"
        this.token++
    }
}

private fun pulseBytes(value: Int): ByteArray = ByteBuffer.allocate(10)
    .order(ByteOrder.LITTLE_ENDIAN)
    .putShort(1)
    .putInt(4)
    .putInt(value)
    .array()

private fun stringField(value: String): ByteArray
{
    val bytes = value.toByteArray(Charsets.UTF_8)
    return byteArrayOf(1, 0) +
        byteArrayOf(bytes.size.toByte(), (bytes.size ushr 8).toByte(),
            (bytes.size ushr 16).toByte(), (bytes.size ushr 24).toByte()) +
        bytes
}

private fun <T> List<T>.indexOfFirstIndexed(predicate: (Int, T) -> Boolean): Int
{
    for (index in indices)
        if (predicate(index, this[index])) return index
    return -1
}

private fun decodePulse(bytes: ByteArray): Pulse
{
    require(bytes.size == 10)
    val input = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
    require(input.short.toInt() == 1 && input.int == 4)
    return Pulse(input.int)
}

fun runKotlinStreamSmoke() = runBlocking {
    val abi = FakeLiftNativeStreamAbi()
    val values = mutableListOf<Int>()
    watchStream(abi, ::decodePulse).collect { pulse ->
        values += pulse.value
        abi.events += "emit-${pulse.value}"
    }
    check(values == listOf(7, 8))
    check(abi.events.count { it == "request" } == 3)
    check(abi.events.indexOf("emit-7") < abi.events.indexOfFirstIndexed { index, event ->
        index > abi.events.indexOf("emit-7") && event == "request"
    })
    check(abi.events.indexOf("emit-8") < abi.events.indexOfFirstIndexed { index, event ->
        index > abi.events.indexOf("emit-8") && event == "request"
    })
    check(abi.events.take(3) == listOf("start", "subscribe", "activate"))
    check(abi.events.takeLast(2) == listOf("unsubscribe", "release"))
    println("PASS Kotlin cold stream handshake and one-credit-per-emit demand")

    for (failingAbi in listOf(FakeLiftNativeStreamAbi(wrongHash = true), FakeLiftNativeStreamAbi(wrongVersion = true)))
    {
        try
        {
            watchStream(failingAbi, ::decodePulse).collect()
            error("stream accepted an old or mismatched runtime handshake")
        }
        catch (expected: LiftStreamException)
        {
            check(failingAbi.events == listOf("start"))
        }
    }

    val activationFailure = FakeLiftNativeStreamAbi(failActivation = true)
    try
    {
        watchStream(activationFailure, ::decodePulse).collect()
        error("failed Activate produced a successful stream")
    }
    catch (expected: LiftStreamException)
    {
        check(activationFailure.events.containsAll(listOf("subscribe", "activate", "unsubscribe")))
        check(activationFailure.events.none { it == "request" })
    }

    val subscribeFailure = FakeLiftNativeStreamAbi(failSubscribe = true)
    try
    {
        watchStream(subscribeFailure, ::decodePulse).collect()
        error("failed Subscribe produced a successful stream")
    }
    catch (expected: LiftStreamException)
    {
        check(subscribeFailure.events == listOf("start", "subscribe"))
    }

    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    val cancelBeforeActivation = FakeLiftNativeStreamAbi()
    val collector = scope.launch(start = CoroutineStart.LAZY) {
        watchStream(cancelBeforeActivation, ::decodePulse).collect()
    }
    cancelBeforeActivation.onSubscribed = { collector.cancel() }
    collector.start()
    collector.join()
    check(cancelBeforeActivation.events.containsAll(listOf("subscribe", "unsubscribe")))
    check("activate" !in cancelBeforeActivation.events && "request" !in cancelBeforeActivation.events)
    println("PASS Kotlin failed-begin cleanup and cancel-before-activation")

    val generatedDtoAbi = FakeLiftNativeStreamAbi()
    val generatedDtoValues = mutableListOf<Int>()
    watchStream(generatedDtoAbi).collect { generatedDtoValues += it.value }
    check(generatedDtoValues == listOf(7, 8))
    check(generatedDtoAbi.events.count { it == "request" } == 3)
    println("PASS generated Kotlin flat-DTO item codec without a caller decoder")

    val scalarAbi = FakeLiftNativeStreamAbi(
        expectedSubscriptionId = 6,
        itemPayloads = listOf(pulseBytes(17)))
    val scalarValues = mutableListOf<Int>()
    numbersStream(scalarAbi).collect { scalarValues += it }
    check(scalarValues == listOf(17))
    println("PASS generated Kotlin scalar item codec without a caller decoder")

    val stringAbi = FakeLiftNativeStreamAbi(
        expectedSubscriptionId = 17,
        itemPayloads = listOf(stringField("Café")))
    val stringValues = mutableListOf<String>()
    labelsStream(stringAbi).collect { stringValues += it }
    check(stringValues == listOf("Café"))
    println("PASS generated Kotlin UTF-8 scalar item codec")

    val nullableAbi = FakeLiftNativeStreamAbi(
        expectedSubscriptionId = 18,
        itemPayloads = listOf(byteArrayOf(1, 0, -1, -1, -1, -1)))
    val nullableValues = mutableListOf<String?>()
    optionalLabelsStream(nullableAbi).collect { nullableValues += it }
    check(nullableValues == listOf(null))
    println("PASS generated Kotlin nullable scalar null sentinel")

    val malformedItems = listOf(
        byteArrayOf(2, 0, 4, 0, 0, 0, 7, 0, 0, 0),
        byteArrayOf(1, 0, 4, 0, 0, 0, 7),
        byteArrayOf(1, 0, 4, 0, 0, 0, 7, 0, 0, 0, 99))
    for (malformedItem in malformedItems)
    {
        val malformedAbi = FakeLiftNativeStreamAbi(itemPayloads = listOf(malformedItem))
        val failure = runCatching { watchStream(malformedAbi).collect() }.exceptionOrNull()
        check(failure is IllegalArgumentException)
        check(malformedAbi.unsubscribeCount == 1 && malformedAbi.releaseCount == 1)
        check(malformedAbi.events.count { it == "request" } == 1)
    }
    val invalidUtf8Abi = FakeLiftNativeStreamAbi(
        expectedSubscriptionId = 17,
        itemPayloads = listOf(byteArrayOf(1, 0, 1, 0, 0, 0, 0xff.toByte())))
    val invalidUtf8Failure = runCatching { labelsStream(invalidUtf8Abi).collect() }.exceptionOrNull()
    check(invalidUtf8Failure is IllegalArgumentException)
    check(invalidUtf8Abi.unsubscribeCount == 1 && invalidUtf8Abi.releaseCount == 1)
    println("PASS Kotlin unknown/truncated/trailing item payloads throw and unsubscribe exactly once")
}
