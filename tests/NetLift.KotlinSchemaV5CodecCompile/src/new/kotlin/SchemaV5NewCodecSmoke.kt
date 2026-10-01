package com.netlift.androidhost.contract

import java.io.File

fun main(args: Array<String>) {
    require(args.size == 1) { "Expected the schema-v5 fixture directory." }
    val fixtures = File(args[0])
    val peerOld = schemaV5Peer(File(fixtures, "peer-old.txt").path)
    val peerNew = schemaV5Peer(File(fixtures, "peer-new.txt").path)
    val oldPayload = File(fixtures, "old-payload.bin").readBytes()
    val newPayload = File(fixtures, "new-payload.bin").readBytes()

    val decodedOld = NetLiftV5DtoCodec.decodePayload(oldPayload, peerOld)
    check(decodedOld.sku == "SKU-OLD" && decodedOld.quantity == 19)
    check(decodedOld.note == "" && decodedOld.nullableNote == null && !decodedOld.giftWrap)

    val decodedNew = NetLiftV5DtoCodec.decodePayload(newPayload, peerNew)
    check(decodedNew.note == "" && decodedNew.nullableNote == null && !decodedNew.giftWrap)
    val encodedNew = NetLiftV5DtoCodec.encodePayload(
        Payload("SKU-NEW", 42, "", null, false))
    check(encodedNew.contentEquals(newPayload)) {
        "Kotlin new-schema bytes differ from managed preflight bytes."
    }

    val emptyOptionalString = NetLiftV5DtoCodec.decodePayload(
        NetLiftV5DtoCodec.encodePayload(
            Payload("SKU-EMPTY", 1, "", "", false)),
        peerNew)
    check(emptyOptionalString.nullableNote == "")

    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(
            replaceSchemaV5Field(newPayload, 1, 0, byteArrayOf()), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(
            replaceSchemaV5Field(newPayload, 1, 1, byteArrayOf(0xff.toByte())), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(
            replaceSchemaV5Field(newPayload, 9, 3, byteArrayOf(2)), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(newPayload.copyOf(newPayload.size - 1), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.BUFFER_TOO_LARGE) {
        NetLiftV5DtoCodec.encodePayload(
            Payload("SKU", 1, "x".repeat(17000), null, false))
    }

    println("PASS Kotlin schema-v5 new DTO defaults, null/empty distinction, codec parity, and malformed NLF1 rejection.")
}
