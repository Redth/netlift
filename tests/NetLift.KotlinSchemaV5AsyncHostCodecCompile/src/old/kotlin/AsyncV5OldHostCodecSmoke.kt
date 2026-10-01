package com.netlift.androidhost.contract

import com.netlift.androidruntime.v5.AsyncHostContextTestMutation
import com.netlift.androidruntime.v5.ManagedResultSchemaContext
import com.netlift.androidruntime.v5.NegotiatedPeerContext
import java.io.File

public fun main(args: Array<String>) {
    require(args.size == 1) { "Expected the async schema-v5 fixture directory." }
    val fixtures = args[0]
    val requestContext = NegotiatedPeerContext.testingOnly("$fixtures/old-request-context.txt")
    val encoded = NetLiftV5AsyncCodec.encodeRequest(SubmitInput("SKU-OLD", 19), requestContext)
    check(encoded.contentEquals(File("$fixtures/old-request.bin").readBytes()))
    check(fieldIDs(encoded) == listOf(1, 2))

    val crossContext = ManagedResultSchemaContext.testingOnly(
        "$fixtures/old-codec-new-managed-result-context.txt")
    val newResult = NetLiftV5AsyncCodec.decodeResult(
        File("$fixtures/new-result.bin").readBytes(), crossContext)
    check(newResult.code == 202 && newResult.accepted)
    val ownContext = ManagedResultSchemaContext.testingOnly("$fixtures/old-result-context.txt")
    val oldResult = NetLiftV5AsyncCodec.decodeResult(
        File("$fixtures/old-result.bin").readBytes(), ownContext)
    check(oldResult.code == 201 && oldResult.accepted)

    rejectRequestContexts(fixtures)
    rejectResultContexts(fixtures, "$fixtures/old-codec-new-managed-result-context.txt")
    expectReject {
        NetLiftV5AsyncCodec.decodeResult(ByteArray(4097), crossContext)
    }
    val invalidBool = File("$fixtures/new-result.bin").readBytes().also {
        it[it.lastIndex] = 2
    }
    expectReject { NetLiftV5AsyncCodec.decodeResult(invalidBool, crossContext) }
    expectReject {
        NetLiftV5AsyncCodec.encodeRequest(
            SubmitInput("x".repeat(4096), 1), requestContext)
    }
    expectReject {
        NetLiftV5AsyncCodec.encodeRequest(
            SubmitInput(String(charArrayOf(0xd800.toChar())), 1), requestContext)
    }
    println("PASS Kotlin async host codec old caller with new/old result peers.")
}

private fun rejectRequestContexts(fixtures: String) {
    val request = SubmitInput("SKU", 1)
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
        val context = NegotiatedPeerContext.testingOnly("$fixtures/old-request-context.txt", mutation)
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
