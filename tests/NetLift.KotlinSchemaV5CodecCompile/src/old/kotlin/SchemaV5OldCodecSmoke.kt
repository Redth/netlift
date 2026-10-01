package com.netlift.androidhost.contract

import java.io.File

fun main(args: Array<String>) {
    require(args.size == 1) { "Expected the schema-v5 fixture directory." }
    val fixtures = File(args[0])
    val peerNew = schemaV5Peer(File(fixtures, "peer-new.txt").path)
    val oldPayload = File(fixtures, "old-payload.bin").readBytes()
    val newPayload = File(fixtures, "new-payload.bin").readBytes()

    val decodedNew = NetLiftV5DtoCodec.decodePayload(newPayload, peerNew)
    check(decodedNew.sku == "SKU-NEW" && decodedNew.quantity == 42)
    check(NetLiftV5DtoCodec.canonicalJson.contains(NetLiftV5DtoCodec.schemaHash))
    val encodedOld = NetLiftV5DtoCodec.encodePayload(Payload("SKU-OLD", 19))
    check(encodedOld.contentEquals(oldPayload)) { "Kotlin old-schema bytes differ from managed preflight bytes." }

    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(newPayload.copyOfRange(0, 9), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.BUFFER_TOO_LARGE) {
        NetLiftV5DtoCodec.decodePayload(ByteArray(65537), peerNew)
    }

    val wrongBodyLength = newPayload.copyOf().also { it[8] = (it[8].toInt() xor 1).toByte() }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(wrongBodyLength, peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(
            appendSchemaV5Field(oldPayload, 11, 1, byteArrayOf(0x78)), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(
            appendSchemaV5Field(oldPayload, 1, 1, byteArrayOf(0x78)), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(
            replaceSchemaV5Field(newPayload, 7, 1, byteArrayOf(0xff.toByte())), peerNew)
    }
    expectSchemaV5Failure(NetLiftV5CodecFailure.MALFORMED) {
        NetLiftV5DtoCodec.decodePayload(
            replaceSchemaV5Field(newPayload, 9, 3, byteArrayOf(2)), peerNew)
    }

    val requiredPeer = NetLiftV5PeerSchema.testingOnly(
        peerNew.schemaHash,
        peerNew.fields + NetLiftV5PeerField.testingOnly(
            11, NetLiftV5FieldKind.UTF8, nullable = false, optional = false))
    expectSchemaV5Failure(NetLiftV5CodecFailure.INVALID_PEER) {
        NetLiftV5DtoCodec.decodePayload(
            appendSchemaV5Field(oldPayload, 11, 1, byteArrayOf(0x78)), requiredPeer)
    }

    println("PASS Kotlin schema-v5 old DTO encoder/decoder, authorized optional skips, and malformed NLF1 rejection.")
}
