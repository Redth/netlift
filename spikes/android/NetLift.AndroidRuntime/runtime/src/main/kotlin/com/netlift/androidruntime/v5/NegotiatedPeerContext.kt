package com.netlift.androidruntime.v5

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.Collections
import java.util.concurrent.atomic.AtomicBoolean

public enum class NegotiatedPeerFieldKind(public val wireCode: Int) {
    UTF8(1),
    I32(2),
    BOOL(3)
}

public class NegotiatedPeerField private constructor(
    public val id: Int,
    public val kind: NegotiatedPeerFieldKind,
    public val nullable: Boolean,
    public val optional: Boolean
) {
    internal companion object {
        @JvmSynthetic
        internal fun issue(
            id: Int,
            kind: NegotiatedPeerFieldKind,
            nullable: Boolean,
            optional: Boolean
        ): NegotiatedPeerField = NegotiatedPeerField(id, kind, nullable, optional)
    }
}

public class NegotiatedPeerContext private constructor(
    public val abiMajor: Long,
    public val schemaVersion: Long,
    public val contractMajor: Long,
    public val contractMinor: Long,
    public val featureBits: Long,
    public val requiredFeatureBits: Long,
    public val serviceId: Long,
    public val methodId: Long,
    public val dtoId: Long,
    public val dtoVersion: Long,
    public val localSchemaHashHex: String,
    public val schemaHashHex: String,
    fields: List<NegotiatedPeerField>,
    private val lease: PeerContextLease
) {
    public val fields: List<NegotiatedPeerField> =
        Collections.unmodifiableList(ArrayList(fields))

    public fun requireActive() {
        check(lease.isActive()) {
            "Negotiated peer context is no longer active because its endpoint is closed."
        }
    }

    internal companion object {
        private const val HEADER_SIZE = 88
        private const val FIELD_SIZE = 4
        private const val MAX_FIELDS = 64
        private const val MAX_SNAPSHOT_SIZE = HEADER_SIZE + MAX_FIELDS * FIELD_SIZE
        private const val SCHEMA_HASH_BYTES = 32
        private const val PEER_SCHEMA_VERSION = 1
        private const val NULLABLE_FLAG = 0x1
        private const val OPTIONAL_FLAG = 0x2
        private const val KNOWN_FLAGS = NULLABLE_FLAG or OPTIONAL_FLAG

        private fun readUInt16(input: ByteBuffer): Int =
            input.short.toInt() and 0xffff

        private fun readUInt32(input: ByteBuffer): Long =
            input.int.toLong() and 0xffff_ffffL

        @JvmSynthetic
        internal fun issue(
            snapshot: ByteArray,
            managedSchemaHashHex: String,
            expectedPeer: V5PeerSchemaIdentity,
            lease: PeerContextLease
        ): NegotiatedPeerContext {
            require(snapshot.size in HEADER_SIZE..MAX_SNAPSHOT_SIZE) {
                "NLPS snapshot length is outside supported bounds."
            }
            require(managedSchemaHashHex.isSha256Hex()) {
                "Managed GetManifest schema hash is not a full SHA-256 value."
            }

            val input = ByteBuffer.wrap(snapshot).order(ByteOrder.LITTLE_ENDIAN)
            require(input.get() == 'N'.code.toByte() &&
                input.get() == 'L'.code.toByte() &&
                input.get() == 'P'.code.toByte() &&
                input.get() == 'S'.code.toByte()
            ) { "NLPS snapshot magic is invalid." }
            require(readUInt16(input) == PEER_SCHEMA_VERSION) {
                "NLPS snapshot version is unsupported."
            }
            require(readUInt16(input) == HEADER_SIZE) {
                "NLPS header size is invalid."
            }
            require(readUInt32(input) == snapshot.size.toLong()) {
                "NLPS declared length does not match its buffer."
            }

            val abiMajor = readUInt32(input)
            val schemaVersion = readUInt32(input)
            val contractMajor = readUInt32(input)
            val contractMinor = readUInt32(input)
            val featureBits = readUInt32(input)
            val requiredFeatureBits = readUInt32(input)
            val serviceId = readUInt32(input)
            val methodId = readUInt32(input)
            val dtoId = readUInt32(input)
            val dtoVersion = readUInt32(input)
            val fieldCount = readUInt16(input)
            val fieldSize = readUInt16(input)
            val schemaHashBytes = ByteArray(SCHEMA_HASH_BYTES)
            input.get(schemaHashBytes)

            require(fieldCount in 1..MAX_FIELDS && fieldSize == FIELD_SIZE &&
                snapshot.size == HEADER_SIZE + fieldCount * FIELD_SIZE &&
                fieldCount == expectedPeer.fields.size
            ) { "NLPS field table length is invalid." }
            require(abiMajor > 0 && schemaVersion > 0 && contractMajor > 0 &&
                serviceId > 0 && methodId > 0 && dtoId > 0 && dtoVersion > 0 &&
                (requiredFeatureBits and featureBits.inv()) == 0L
            ) { "NLPS fixed metadata contains invalid values." }

            val peerHashHex = schemaHashBytes.toHex()
            require(peerHashHex == expectedPeer.schemaHashHex &&
                MessageDigest.isEqual(
                    schemaHashBytes,
                    expectedPeer.schemaHashHex.hexToBytes()
                )
            ) { "NLPS full peer hash differs from the trusted compatible-start peer." }
            require(abiMajor == expectedPeer.abiMajor &&
                schemaVersion == expectedPeer.schemaVersion &&
                contractMajor == expectedPeer.contractMajor &&
                contractMinor == expectedPeer.contractMinor &&
                featureBits == expectedPeer.featureBits &&
                requiredFeatureBits == expectedPeer.requiredFeatureBits &&
                serviceId == expectedPeer.serviceId &&
                methodId == expectedPeer.methodId &&
                dtoId == expectedPeer.dtoId &&
                dtoVersion == expectedPeer.dtoVersion
            ) { "NLPS metadata differs from the verified peer manifest." }

            val peerFields = ArrayList<NegotiatedPeerField>(fieldCount)
            var previousId = 0
            repeat(fieldCount) { index ->
                val fieldId = readUInt16(input)
                val kindCode = input.get().toInt() and 0xff
                val flags = input.get().toInt() and 0xff
                val kind = NegotiatedPeerFieldKind.entries.firstOrNull {
                    it.wireCode == kindCode
                } ?: throw IllegalArgumentException("NLPS field kind is unsupported.")
                val nullable = (flags and NULLABLE_FLAG) != 0
                val optional = (flags and OPTIONAL_FLAG) != 0
                require(fieldId > previousId && (flags and KNOWN_FLAGS.inv()) == 0 &&
                    (!nullable || kind == NegotiatedPeerFieldKind.UTF8)
                ) { "NLPS field IDs, kinds, or flags are invalid." }
                val expectedField = expectedPeer.fields[index]
                require(fieldId == expectedField.id && kind == expectedField.kind &&
                    nullable == expectedField.nullable && optional == expectedField.optional
                ) { "NLPS field record differs from the verified peer manifest." }
                previousId = fieldId
                peerFields.add(
                    NegotiatedPeerField.issue(fieldId, kind, nullable, optional)
                )
            }
            require(peerFields.size == expectedPeer.fields.size) {
                "NLPS field table differs from the verified peer manifest."
            }

            return NegotiatedPeerContext(
                abiMajor,
                schemaVersion,
                contractMajor,
                contractMinor,
                featureBits,
                requiredFeatureBits,
                serviceId,
                methodId,
                dtoId,
                dtoVersion,
                managedSchemaHashHex.lowercase(),
                peerHashHex,
                peerFields,
                lease
            )
        }
    }
}

internal data class V5PeerFieldDescriptor(
    val id: Int,
    val kind: NegotiatedPeerFieldKind,
    val nullable: Boolean,
    val optional: Boolean
)

internal data class V5PeerSchemaIdentity(
    val schemaHashHex: String,
    val abiMajor: Long,
    val schemaVersion: Long,
    val contractMajor: Long,
    val contractMinor: Long,
    val featureBits: Long,
    val requiredFeatureBits: Long,
    val serviceId: Long,
    val methodId: Long,
    val dtoId: Long,
    val dtoVersion: Long,
    val fields: List<V5PeerFieldDescriptor>
)

internal class PeerContextLease private constructor() {
    private val active = AtomicBoolean(true)

    @JvmSynthetic
    fun isActive(): Boolean = active.get()

    @JvmSynthetic
    fun invalidate() {
        active.set(false)
    }

    internal companion object {
        @JvmSynthetic
        internal fun create(): PeerContextLease = PeerContextLease()
    }
}

internal fun String.isSha256Hex(): Boolean =
    length == 64 && all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }

internal fun String.hexToBytes(): ByteArray {
    require(isSha256Hex()) { "SHA-256 value must contain exactly 64 hexadecimal characters." }
    return chunked(2).map { it.toInt(16).toByte() }.toByteArray()
}

private fun ByteArray.toHex(): String =
    joinToString(separator = "") { "%02x".format(it.toInt() and 0xff) }
