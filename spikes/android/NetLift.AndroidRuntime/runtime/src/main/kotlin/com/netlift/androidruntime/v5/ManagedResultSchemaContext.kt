package com.netlift.androidruntime.v5

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.Collections
import java.util.concurrent.atomic.AtomicBoolean

public class ManagedResultSchemaContext private constructor(
    public val profileId: Long,
    public val abiMajor: Long,
    public val schemaVersion: Long,
    public val contractMajor: Long,
    public val contractMinor: Long,
    public val featureBits: Long,
    public val requiredFeatureBits: Long,
    public val serviceId: Long,
    public val methodId: Long,
    public val resultDtoId: Long,
    public val resultDtoVersion: Long,
    public val localSchemaHashHex: String,
    public val peerSchemaHashHex: String,
    fields: List<NegotiatedPeerField>,
    private val endpointToken: Long,
    private val endpointLease: ManagedResultSchemaLease
) {
    public val fields: List<NegotiatedPeerField> =
        Collections.unmodifiableList(ArrayList(fields))

    public fun requireActive() {
        check(
            endpointLease.matches(
                endpointToken,
                profileId,
                serviceId,
                methodId,
                resultDtoId,
                resultDtoVersion
            )
        ) {
            "Managed result schema context is no longer active for its endpoint and route."
        }
    }

    internal companion object {
        private const val HEADER_SIZE = 136
        private const val FIELD_SIZE = 4
        private const val MAX_FIELDS = 64
        private const val MAX_SNAPSHOT_SIZE = HEADER_SIZE + MAX_FIELDS * FIELD_SIZE
        private const val SCHEMA_HASH_BYTES = 32
        private const val FORMAT_VERSION = 1
        private const val RESULT_DIRECTION = 1
        private const val ASYNC_CALLBACK_PROFILE = 1
        private const val REQUIRED_ASYNC_FEATURES = 0x36L
        private const val NULLABLE_FLAG = 0x1
        private const val OPTIONAL_FLAG = 0x2
        private const val KNOWN_FLAGS = NULLABLE_FLAG or OPTIONAL_FLAG
        private const val UINT32_MAX = 0xffff_ffffL

        private fun readUInt16(input: ByteBuffer): Int =
            input.short.toInt() and 0xffff

        private fun readUInt32(input: ByteBuffer): Long =
            input.int.toLong() and UINT32_MAX

        @JvmSynthetic
        internal fun issue(
            snapshot: ByteArray,
            expected: ManagedResultSchemaExpectation
        ): ManagedResultSchemaContext {
            require(snapshot.size in HEADER_SIZE..MAX_SNAPSHOT_SIZE) {
                "NLMR snapshot length is outside supported bounds."
            }
            val snapshotCopy = snapshot.copyOf()
            validateExpected(expected)
            check(expected.endpointLease.matches(
                expected.endpointToken,
                expected.profileId,
                expected.serviceId,
                expected.methodId,
                expected.resultDtoId,
                expected.resultDtoVersion
            )) {
                "NLMR authority is not active for its endpoint and route."
            }

            val input = ByteBuffer.wrap(snapshotCopy).order(ByteOrder.LITTLE_ENDIAN)
            val magic = ByteArray(4)
            input.get(magic)
            require(magic.contentEquals(byteArrayOf(
                'N'.code.toByte(),
                'L'.code.toByte(),
                'M'.code.toByte(),
                'R'.code.toByte()
            ))) { "NLMR snapshot magic is invalid." }
            require(readUInt16(input) == FORMAT_VERSION) {
                "NLMR snapshot version is unsupported."
            }
            require(readUInt16(input) == HEADER_SIZE) {
                "NLMR header size is invalid."
            }
            require(readUInt32(input) == snapshotCopy.size.toLong()) {
                "NLMR declared length does not match its buffer."
            }
            require(readUInt32(input) == RESULT_DIRECTION.toLong()) {
                "NLMR direction is not managed-result-to-native."
            }

            val endpointToken = input.long
            val profileId = readUInt32(input)
            val abiMajor = readUInt32(input)
            val schemaVersion = readUInt32(input)
            val contractMajor = readUInt32(input)
            val contractMinor = readUInt32(input)
            val featureBits = readUInt32(input)
            val requiredFeatureBits = readUInt32(input)
            val serviceId = readUInt32(input)
            val methodId = readUInt32(input)
            val resultDtoId = readUInt32(input)
            val resultDtoVersion = readUInt32(input)
            val fieldCount = readUInt16(input)
            val fieldSize = readUInt16(input)
            val localSchemaHashBytes = ByteArray(SCHEMA_HASH_BYTES)
            input.get(localSchemaHashBytes)
            val peerSchemaHashBytes = ByteArray(SCHEMA_HASH_BYTES)
            input.get(peerSchemaHashBytes)

            require(fieldCount in 1..MAX_FIELDS && fieldSize == FIELD_SIZE &&
                snapshotCopy.size == HEADER_SIZE + fieldCount * FIELD_SIZE &&
                fieldCount == expected.fields.size
            ) { "NLMR field table length is invalid." }
            require(endpointToken == expected.endpointToken &&
                profileId == expected.profileId &&
                abiMajor == expected.abiMajor &&
                schemaVersion == expected.schemaVersion &&
                contractMajor == expected.contractMajor &&
                contractMinor == expected.contractMinor &&
                featureBits == expected.featureBits &&
                requiredFeatureBits == expected.requiredFeatureBits &&
                serviceId == expected.serviceId &&
                methodId == expected.methodId &&
                resultDtoId == expected.resultDtoId &&
                resultDtoVersion == expected.resultDtoVersion
            ) { "NLMR metadata differs from its live endpoint and local route." }

            val expectedLocalHashBytes = expected.localSchemaHashHex.hexToBytes()
            val expectedPeerHashBytes = expected.peerSchemaHashHex.hexToBytes()
            require(MessageDigest.isEqual(localSchemaHashBytes, expectedLocalHashBytes)) {
                "NLMR managed-local hash differs from the trusted local schema."
            }
            require(MessageDigest.isEqual(peerSchemaHashBytes, expectedPeerHashBytes)) {
                "NLMR native-peer hash differs from the negotiated peer schema."
            }

            val resultFields = ArrayList<NegotiatedPeerField>(fieldCount)
            var previousId = 0
            repeat(fieldCount) { index ->
                val fieldId = readUInt16(input)
                val kindCode = input.get().toInt() and 0xff
                val flags = input.get().toInt() and 0xff
                val kind = NegotiatedPeerFieldKind.entries.firstOrNull {
                    it.wireCode == kindCode
                } ?: throw IllegalArgumentException("NLMR field kind is unsupported.")
                val nullable = (flags and NULLABLE_FLAG) != 0
                val optional = (flags and OPTIONAL_FLAG) != 0
                require(fieldId > previousId && (flags and KNOWN_FLAGS.inv()) == 0 &&
                    (!nullable || kind == NegotiatedPeerFieldKind.UTF8)
                ) { "NLMR field IDs, kinds, or flags are invalid." }

                val expectedField = expected.fields[index]
                require(fieldId == expectedField.id && kind == expectedField.kind &&
                    nullable == expectedField.nullable && optional == expectedField.optional
                ) { "NLMR field record differs from the managed-local result DTO." }
                previousId = fieldId
                resultFields.add(
                    NegotiatedPeerField.issue(fieldId, kind, nullable, optional)
                )
            }

            val result = ManagedResultSchemaContext(
                profileId,
                abiMajor,
                schemaVersion,
                contractMajor,
                contractMinor,
                featureBits,
                requiredFeatureBits,
                serviceId,
                methodId,
                resultDtoId,
                resultDtoVersion,
                localSchemaHashBytes.toHex(),
                peerSchemaHashBytes.toHex(),
                resultFields,
                endpointToken,
                expected.endpointLease
            )
            result.requireActive()
            return result
        }

        private fun validateExpected(expected: ManagedResultSchemaExpectation) {
            require(expected.endpointToken > 0) {
                "NLMR endpoint token must be positive."
            }
            require(expected.profileId == ASYNC_CALLBACK_PROFILE.toLong()) {
                "NLMR authority must use async-callback-v1."
            }
            require(expected.abiMajor in 1..UINT32_MAX &&
                expected.schemaVersion in 1..UINT32_MAX &&
                expected.contractMajor in 1..UINT32_MAX &&
                expected.contractMinor in 0..UINT32_MAX &&
                expected.featureBits in 0..UINT32_MAX &&
                expected.requiredFeatureBits in 0..UINT32_MAX &&
                (expected.requiredFeatureBits and expected.featureBits.inv()) == 0L &&
                (expected.featureBits and REQUIRED_ASYNC_FEATURES) == REQUIRED_ASYNC_FEATURES &&
                (expected.requiredFeatureBits and REQUIRED_ASYNC_FEATURES) ==
                    REQUIRED_ASYNC_FEATURES &&
                expected.serviceId in 1..UINT32_MAX &&
                expected.methodId in 1..UINT32_MAX &&
                expected.resultDtoId in 1..UINT32_MAX &&
                expected.resultDtoVersion in 1..UINT32_MAX
            ) { "NLMR authority metadata is outside the supported async profile." }
            require(expected.localSchemaHashHex.isSha256Hex() &&
                expected.peerSchemaHashHex.isSha256Hex()
            ) { "NLMR authority hashes must be full SHA-256 values." }
            require(expected.fields.size in 1..MAX_FIELDS) {
                "NLMR authority field count is outside supported bounds."
            }

            var previousId = 0
            for (field in expected.fields) {
                require(field.id in 1..0xffff && field.id > previousId &&
                    (!field.nullable || field.kind == NegotiatedPeerFieldKind.UTF8)
                ) { "NLMR authority fields must be valid, unique, and ascending." }
                previousId = field.id
            }
        }
    }
}

internal class ManagedResultSchemaExpectation private constructor(
    internal val endpointToken: Long,
    internal val profileId: Long,
    internal val abiMajor: Long,
    internal val schemaVersion: Long,
    internal val contractMajor: Long,
    internal val contractMinor: Long,
    internal val featureBits: Long,
    internal val requiredFeatureBits: Long,
    internal val serviceId: Long,
    internal val methodId: Long,
    internal val resultDtoId: Long,
    internal val resultDtoVersion: Long,
    internal val localSchemaHashHex: String,
    internal val peerSchemaHashHex: String,
    fields: List<NegotiatedPeerField>,
    internal val endpointLease: ManagedResultSchemaLease
) {
    internal val fields: List<NegotiatedPeerField> =
        Collections.unmodifiableList(ArrayList(fields))

    internal companion object {
        @JvmSynthetic
        internal fun create(
            endpointToken: Long,
            profileId: Long,
            abiMajor: Long,
            schemaVersion: Long,
            contractMajor: Long,
            contractMinor: Long,
            featureBits: Long,
            requiredFeatureBits: Long,
            serviceId: Long,
            methodId: Long,
            resultDtoId: Long,
            resultDtoVersion: Long,
            localSchemaHashHex: String,
            peerSchemaHashHex: String,
            fields: List<NegotiatedPeerField>,
            endpointLease: ManagedResultSchemaLease
        ): ManagedResultSchemaExpectation =
            ManagedResultSchemaExpectation(
                endpointToken,
                profileId,
                abiMajor,
                schemaVersion,
                contractMajor,
                contractMinor,
                featureBits,
                requiredFeatureBits,
                serviceId,
                methodId,
                resultDtoId,
                resultDtoVersion,
                localSchemaHashHex,
                peerSchemaHashHex,
                fields,
                endpointLease
            )
    }
}

internal class ManagedResultSchemaLease private constructor(
    private val endpointToken: Long,
    private val profileId: Long,
    private val serviceId: Long,
    private val methodId: Long,
    private val resultDtoId: Long,
    private val resultDtoVersion: Long
) {
    private val active = AtomicBoolean(true)

    @JvmSynthetic
    internal fun matches(
        endpointToken: Long,
        profileId: Long,
        serviceId: Long,
        methodId: Long,
        resultDtoId: Long,
        resultDtoVersion: Long
    ): Boolean =
        active.get() &&
            this.endpointToken == endpointToken &&
            this.profileId == profileId &&
            this.serviceId == serviceId &&
            this.methodId == methodId &&
            this.resultDtoId == resultDtoId &&
            this.resultDtoVersion == resultDtoVersion

    @JvmSynthetic
    internal fun invalidate() {
        active.set(false)
    }

    internal companion object {
        @JvmSynthetic
        internal fun create(
            endpointToken: Long,
            profileId: Long,
            serviceId: Long,
            methodId: Long,
            resultDtoId: Long,
            resultDtoVersion: Long
        ): ManagedResultSchemaLease =
            ManagedResultSchemaLease(
                endpointToken,
                profileId,
                serviceId,
                methodId,
                resultDtoId,
                resultDtoVersion
            )
    }
}

private fun ByteArray.toHex(): String =
    joinToString(separator = "") { "%02x".format(it.toInt() and 0xff) }
