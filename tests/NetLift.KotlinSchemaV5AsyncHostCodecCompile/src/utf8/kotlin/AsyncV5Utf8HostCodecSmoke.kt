package com.netlift.androidhost.contract

import com.netlift.androidruntime.v5.ManagedResultSchemaContext
import java.io.File

public fun main(args: Array<String>) {
    require(args.size == 1) { "Expected the async schema-v5 fixture directory." }
    val fixtures = args[0]
    val context = ManagedResultSchemaContext.testingOnly("$fixtures/utf8-result-context.txt")
    val valid = NetLiftV5AsyncCodec.decodeResult(
        File("$fixtures/utf8-result.bin").readBytes(), context)
    check(valid.code == "utf8-ok" && valid.accepted && !valid.audited)
    expectReject {
        NetLiftV5AsyncCodec.decodeResult(
            File("$fixtures/utf8-invalid-result.bin").readBytes(), context)
    }
    println("PASS Kotlin async host codec strict UTF-8 result decoding.")
}

private inline fun expectReject(action: () -> Unit) {
    try {
        action()
    } catch (_: Exception) {
        return
    }
    error("Expected invalid UTF-8 result rejection.")
}
