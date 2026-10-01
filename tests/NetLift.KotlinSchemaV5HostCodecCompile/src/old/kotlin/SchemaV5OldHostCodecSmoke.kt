package com.netlift.androidhost.contract

import com.netlift.androidruntime.v5.NegotiatedPeerContext
import com.netlift.androidruntime.v5.PeerContextTestMutation
import java.io.File

public fun main(args: Array<String>) {
    require(args.size == 1) { "Expected the schema-v5 host-codec fixture directory." }
    val fixtures = args[0]
    val contextPath = "$fixtures/new-to-old-context.txt"
    val mismatchedContextPath = "$fixtures/old-to-new-context.txt"
    val context = NegotiatedPeerContext.testingOnly(contextPath)
    check(context.localSchemaHashHex != NetLiftV5DtoCodec.schemaHash)
    val oldPayload = File("$fixtures/old-payload.bin").readBytes()

    val encodedOld = NetLiftV5DtoCodec.encodePayload(Payload("SKU-OLD", 19), context)
    check(encodedOld.contentEquals(oldPayload))
    check(fieldIDs(encodedOld) == listOf(1, 2))
    expectReject { NetLiftV5DtoCodec.encodePayload(Payload("x".repeat(17000), 1), context) }
    val mismatchedContext = NegotiatedPeerContext.testingOnly(mismatchedContextPath)
    expectReject { NetLiftV5DtoCodec.encodePayload(Payload("SKU", 1), mismatchedContext) }
    rejectInvalidContexts(contextPath)
    println("PASS Kotlin host-bound v5 old native codec with newer managed host.")
}

private fun rejectInvalidContexts(path: String) {
    PeerContextTestMutation.entries.filter { it != PeerContextTestMutation.NONE }.forEach { mutation ->
        val context = NegotiatedPeerContext.testingOnly(path, mutation)
        expectReject { NetLiftV5DtoCodec.encodePayload(Payload("SKU", 1), context) }
    }
}

private inline fun expectReject(action: () -> Unit) {
    try {
        action()
    } catch (_: Exception) {
        return
    }
    error("Expected host-bound schema-v5 codec rejection.")
}

private fun fieldIDs(bytes: ByteArray): List<Int> {
    if (bytes.size < 12) return emptyList()
    val ids = mutableListOf<Int>()
    var offset = 12
    while (offset < bytes.size) {
        if (bytes.size - offset < 7) return emptyList()
        val id = (bytes[offset].toInt() and 0xff) or
            ((bytes[offset + 1].toInt() and 0xff) shl 8)
        val length = (0..3).fold(0L) { value, shift ->
            value or ((bytes[offset + 3 + shift].toLong() and 0xffL) shl (shift * 8))
        }.toInt()
        if (length > bytes.size - offset - 7) return emptyList()
        ids.add(id)
        offset += 7 + length
    }
    return ids
}
