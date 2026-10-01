package com.netlift.androidhost.contract

import com.netlift.androidruntime.v5.AsyncHostContextTestMutation
import com.netlift.androidruntime.v5.ManagedResultSchemaContext
import com.netlift.androidruntime.v5.NegotiatedPeerContext
import java.io.File

public fun main(args: Array<String>) {
    require(args.size == 1) { "Expected the async schema-v5 fixture directory." }
    val fixtures = args[0]
    val requestContext = NegotiatedPeerContext.testingOnly("$fixtures/new-request-context.txt")
    val encoded = NetLiftV5AsyncCodec.encodeRequest(
        SubmitInput("SKU-NEW", 42, "note"), requestContext)
    check(encoded.contentEquals(File("$fixtures/new-request.bin").readBytes()))
    check(fieldIDs(encoded) == listOf(1, 2, 7))
    val defaultRequest = NetLiftV5AsyncCodec.encodeRequest(
        SubmitInput("SKU-DEFAULT", 1, ""), requestContext)
    check(defaultRequest.contentEquals(File("$fixtures/new-request-default.bin").readBytes()))
    check(fieldIDs(defaultRequest) == listOf(1, 2, 7))

    val crossContext = ManagedResultSchemaContext.testingOnly(
        "$fixtures/new-codec-old-managed-result-context.txt")
    val oldResult = NetLiftV5AsyncCodec.decodeResult(
        File("$fixtures/old-result.bin").readBytes(), crossContext)
    check(oldResult.code == 201 && oldResult.accepted && !oldResult.audited)
    val ownContext = ManagedResultSchemaContext.testingOnly("$fixtures/new-result-context.txt")
    val newResult = NetLiftV5AsyncCodec.decodeResult(
        File("$fixtures/new-result.bin").readBytes(), ownContext)
    check(newResult.code == 202 && newResult.accepted && newResult.audited)

    rejectRequestContexts(fixtures)
    rejectResultContexts(fixtures, "$fixtures/new-result-context.txt")
    expectReject {
        NetLiftV5AsyncCodec.decodeResult(ByteArray(4097), ownContext)
    }
    val invalidBool = File("$fixtures/new-result.bin").readBytes().also {
        it[it.lastIndex] = 2
    }
    expectReject { NetLiftV5AsyncCodec.decodeResult(invalidBool, ownContext) }
    val duplicate = File("$fixtures/new-result.bin").readBytes().also {
        it[23] = 1
        it[24] = 0
    }
    expectReject { NetLiftV5AsyncCodec.decodeResult(duplicate, ownContext) }
    val badLength = File("$fixtures/new-result.bin").readBytes().also {
        it[15] = 0xff.toByte()
    }
    expectReject { NetLiftV5AsyncCodec.decodeResult(badLength, ownContext) }
    expectReject {
        NetLiftV5AsyncCodec.encodeRequest(
            SubmitInput("x".repeat(4096), 1, ""), requestContext)
    }
    expectReject {
        NetLiftV5AsyncCodec.encodeRequest(
            SubmitInput(String(charArrayOf(0xd800.toChar())), 1, ""), requestContext)
    }
    println("PASS Kotlin async host codec new caller with old/new result peers.")
}

private fun rejectRequestContexts(fixtures: String) {
    val request = SubmitInput("SKU", 1, "")
    val invalid = listOf(
        AsyncHostContextTestMutation.INACTIVE,
        AsyncHostContextTestMutation.INVALID_LOCAL_HASH,
        AsyncHostContextTestMutation.WRONG_PEER_HASH,
        AsyncHostContextTestMutation.WRONG_ABI,
        AsyncHostContextTestMutation.WRONG_FEATURES,
        AsyncHostContextTestMutation.WRONG_CONTRACT,
        AsyncHostContextTestMutation.WRONG_SERVICE,
        AsyncHostContextTestMutation.WRONG_METHOD,
        AsyncHostContextTestMutation.WRONG_DTO,
        AsyncHostContextTestMutation.WRONG_DTO_VERSION,
        AsyncHostContextTestMutation.UNKNOWN_REQUIRED_FIELD,
        AsyncHostContextTestMutation.INCOMPATIBLE_KIND,
        AsyncHostContextTestMutation.DUPLICATE_FIELD,
        AsyncHostContextTestMutation.MISSING_REQUIRED_FIELD
    )
    invalid.forEach { mutation ->
        val context = NegotiatedPeerContext.testingOnly("$fixtures/new-request-context.txt", mutation)
        expectReject { NetLiftV5AsyncCodec.encodeRequest(request, context) }
    }
}

private fun rejectResultContexts(fixtures: String, contextPath: String) {
    val payload = File("$fixtures/new-result.bin").readBytes()
    val invalid = listOf(
        AsyncHostContextTestMutation.INACTIVE,
        AsyncHostContextTestMutation.INVALID_LOCAL_HASH,
        AsyncHostContextTestMutation.WRONG_PEER_HASH,
        AsyncHostContextTestMutation.WRONG_PROFILE,
        AsyncHostContextTestMutation.WRONG_ABI,
        AsyncHostContextTestMutation.WRONG_FEATURES,
        AsyncHostContextTestMutation.WRONG_CONTRACT,
        AsyncHostContextTestMutation.WRONG_SERVICE,
        AsyncHostContextTestMutation.WRONG_METHOD,
        AsyncHostContextTestMutation.WRONG_DTO,
        AsyncHostContextTestMutation.WRONG_DTO_VERSION,
        AsyncHostContextTestMutation.UNKNOWN_REQUIRED_FIELD,
        AsyncHostContextTestMutation.INCOMPATIBLE_KIND,
        AsyncHostContextTestMutation.DUPLICATE_FIELD,
        AsyncHostContextTestMutation.MISSING_REQUIRED_FIELD
    )
    invalid.forEach { mutation ->
        val context = ManagedResultSchemaContext.testingOnly(contextPath, mutation)
        expectReject { NetLiftV5AsyncCodec.decodeResult(payload, context) }
    }
}

private inline fun expectReject(action: () -> Unit) {
    try {
        action()
    } catch (_: Exception) {
        return
    }
    error("Expected async schema-v5 host codec rejection.")
}

private fun fieldIDs(bytes: ByteArray): List<Int> {
    if (bytes.size < 12) return emptyList()
    val ids = mutableListOf<Int>()
    var offset = 12
    while (offset < bytes.size) {
        if (bytes.size - offset < 7) return emptyList()
        val id = (bytes[offset].toInt() and 0xff) or
            ((bytes[offset + 1].toInt() and 0xff) shl 8)
        val length = (0..3).fold(0L) { result, index ->
            result or ((bytes[offset + 3 + index].toLong() and 0xffL) shl (index * 8))
        }
        if (length > bytes.size - offset - 7) return emptyList()
        ids.add(id)
        offset += 7 + length.toInt()
    }
    return ids
}
