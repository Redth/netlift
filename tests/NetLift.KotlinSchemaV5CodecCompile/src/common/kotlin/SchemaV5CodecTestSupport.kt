package com.netlift.androidhost.contract

import java.io.ByteArrayOutputStream
import java.io.File

internal fun schemaV5Peer(path: String): NetLiftV5PeerSchema {
    var schemaHash: String? = null
    val fields = mutableListOf<NetLiftV5PeerField>()
    for (line in File(path).readLines()) {
        val (key, value) = line.split('=', limit = 2).takeIf { it.size == 2 }
            ?: error("Malformed peer descriptor line.")
        when (key) {
            "schemaHash" -> schemaHash = value
            "field" -> {
                val parts = value.split('|')
                require(parts.size == 4) { "Malformed peer field descriptor." }
                val kind = when (parts[1]) {
                    "utf8" -> NetLiftV5FieldKind.UTF8
                    "i32" -> NetLiftV5FieldKind.I32
                    "bool" -> NetLiftV5FieldKind.BOOL
                    else -> error("Unknown schema-v5 field kind.")
                }
                fields += NetLiftV5PeerField.testingOnly(
                    parts[0].toInt(),
                    kind,
                    parts[2] == "1",
                    parts[3] == "1")
            }
            else -> error("Unknown peer descriptor key.")
        }
    }
    return NetLiftV5PeerSchema.testingOnly(
        requireNotNull(schemaHash) { "Peer descriptor does not contain its schema hash." },
        fields)
}

internal fun expectSchemaV5Failure(
    expected: NetLiftV5CodecFailure,
    action: () -> Unit
) {
    try {
        action()
    } catch (error: NetLiftV5CodecException) {
        check(error.reason == expected) { "Expected $expected, got ${error.reason}." }
        return
    }
    error("Expected schema-v5 codec failure $expected.")
}

internal fun appendSchemaV5Field(
    source: ByteArray,
    id: Int,
    kind: Int,
    payload: ByteArray
): ByteArray {
    val result = ByteArrayOutputStream()
    result.write(source)
    result.write(id and 0xff)
    result.write((id ushr 8) and 0xff)
    result.write(kind)
    writeU32(result, payload.size)
    result.write(payload)
    val output = result.toByteArray()
    setU16(output, u16(output, 6) + 1, 6)
    setU32(output, output.size - 12, 8)
    return output
}

internal fun replaceSchemaV5Field(
    source: ByteArray,
    id: Int,
    kind: Int,
    payload: ByteArray
): ByteArray {
    require(source.size >= 12)
    val output = ByteArrayOutputStream()
    output.write(source, 0, 12)
    var offset = 12
    while (offset < source.size) {
        require(source.size - offset >= 7)
        val currentId = u16(source, offset)
        val length = u32(source, offset + 3)
        require(length <= source.size - offset - 7)
        if (currentId == id) {
            output.write(source[offset].toInt() and 0xff)
            output.write(source[offset + 1].toInt() and 0xff)
            output.write(kind)
            writeU32(output, payload.size)
            output.write(payload)
        } else {
            output.write(source, offset, 7 + length)
        }
        offset += 7 + length
    }
    val result = output.toByteArray()
    setU32(result, result.size - 12, 8)
    return result
}

private fun writeU32(output: ByteArrayOutputStream, value: Int) {
    repeat(4) { shift -> output.write((value ushr (shift * 8)) and 0xff) }
}

private fun u16(bytes: ByteArray, offset: Int): Int =
    (bytes[offset].toInt() and 0xff) or ((bytes[offset + 1].toInt() and 0xff) shl 8)

private fun u32(bytes: ByteArray, offset: Int): Int =
    (0..3).fold(0) { value, index ->
        value or ((bytes[offset + index].toInt() and 0xff) shl (index * 8))
    }

private fun setU16(bytes: ByteArray, value: Int, offset: Int) {
    bytes[offset] = value.toByte()
    bytes[offset + 1] = (value ushr 8).toByte()
}

private fun setU32(bytes: ByteArray, value: Int, offset: Int) {
    repeat(4) { shift -> bytes[offset + shift] = (value ushr (shift * 8)).toByte() }
}
