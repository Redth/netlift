package com.netlift.androidhost.contract

import com.netlift.androidruntime.v5.NegotiatedPeerContext
import com.netlift.androidruntime.v5.PeerContextTestMutation
import java.io.File

public fun main(args: Array<String>) {
    require(args.size == 1) { "Expected the schema-v5 host-codec fixture directory." }
    val fixtures = args[0]
    val contextPath = "$fixtures/old-to-new-context.txt"
    val mismatchedContextPath = "$fixtures/new-to-old-context.txt"
    val context = NegotiatedPeerContext.testingOnly(contextPath)
    check(context.localSchemaHashHex != NetLiftV5DtoCodec.schemaHash)
    val newPayload = File("$fixtures/new-payload.bin").readBytes()

    val newValue = Payload("SKU-NEW", 42, "", null, false)
    val encodedNew = NetLiftV5DtoCodec.encodePayload(newValue, context)
    check(encodedNew.contentEquals(newPayload))
    check(fieldIDs(encodedNew) == listOf(1, 2, 7, 8, 9))
    val empty = NetLiftV5DtoCodec.encodePayload(Payload("SKU-EMPTY", 1, "", "", false), context)
    check(!empty.contentEquals(newPayload)) { "Empty nullable UTF-8 must remain distinct from null." }
    expectReject {
        NetLiftV5DtoCodec.encodePayload(
            Payload("SKU", 1, String(charArrayOf(0xd800.toChar())), null, false), context)
    }

    expectReject {
        NetLiftV5DtoCodec.encodePayload(Payload("SKU", 1, "x".repeat(17000), null, false), context)
    }
    val mismatchedContext = NegotiatedPeerContext.testingOnly(mismatchedContextPath)
    expectReject {
        NetLiftV5DtoCodec.encodePayload(Payload("SKU", 1, "", null, false), mismatchedContext)
    }
    rejectInvalidContexts(contextPath)
    println("PASS Kotlin host-bound v5 new native codec with older managed host.")
}

private fun rejectInvalidContexts(path: String) {
    PeerContextTestMutation.entries.filter { it != PeerContextTestMutation.NONE }.forEach { mutation ->
        val context = NegotiatedPeerContext.testingOnly(path, mutation)
        expectReject {
            NetLiftV5DtoCodec.encodePayload(Payload("SKU", 1, "", null, false), context)
        }
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
