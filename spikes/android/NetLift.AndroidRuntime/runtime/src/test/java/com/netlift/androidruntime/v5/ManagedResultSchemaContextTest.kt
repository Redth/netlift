package com.netlift.androidruntime.v5

import java.lang.reflect.Modifier
import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

internal class ManagedResultSchemaContextTest {
    @Test
    fun validatesManagedResultDescriptorAndKeepsItsFieldsImmutable() {
        val fixture = Fixture()
        val context = fixture.issue(fixture.snapshot())

        assertEquals(1L, context.profileId)
        assertEquals(1L, context.abiMajor)
        assertEquals(5L, context.schemaVersion)
        assertEquals(2L, context.contractMajor)
        assertEquals(3L, context.contractMinor)
        assertEquals(0x36L, context.featureBits)
        assertEquals(0x36L, context.requiredFeatureBits)
        assertEquals(410L, context.serviceId)
        assertEquals(7L, context.methodId)
        assertEquals(12L, context.resultDtoId)
        assertEquals(2L, context.resultDtoVersion)
        assertEquals(LOCAL_HASH, context.localSchemaHashHex)
        assertEquals(PEER_HASH, context.peerSchemaHashHex)
        assertEquals(
            listOf(1, 4, 9),
            context.fields.map(NegotiatedPeerField::id)
        )
        context.requireActive()
        assertThrows(UnsupportedOperationException::class.java) {
            @Suppress("UNCHECKED_CAST")
            (context.fields as MutableList<NegotiatedPeerField>).add(
                NegotiatedPeerField.issue(
                    10,
                    NegotiatedPeerFieldKind.BOOL,
                    false,
                    false
                )
            )
        }
    }

    @Test
    fun rejectsMalformedHeaderAndBounds() {
        val fixture = Fixture()
        assertInvalid(fixture, fixture.snapshot().copyOfRange(0, 135))
        assertInvalid(fixture, fixture.snapshot().also { it[0] = 'X'.code.toByte() })
        assertInvalid(fixture, fixture.snapshot().also { it.putU16(4, 2) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU16(6, 132) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU32(8, 999) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU32(12, 2) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU32(24, 2) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU16(70, 8) })
    }

    @Test
    fun rejectsForeignEndpointAndWrongRouteOrResultDto() {
        val fixture = Fixture()
        assertInvalid(fixture, fixture.snapshot().also { it.putU64(16, 991) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU32(52, 411) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU32(56, 8) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU32(60, 13) })
        assertInvalid(fixture, fixture.snapshot().also { it.putU32(64, 3) })
    }

    @Test
    fun rejectsEitherHashWhenItDiffersFromItsAuthority() {
        val fixture = Fixture()
        assertInvalid(fixture, fixture.snapshot().also { it[72] = (it[72].toInt() xor 1).toByte() })
        assertInvalid(fixture, fixture.snapshot().also { it[104] = (it[104].toInt() xor 1).toByte() })
    }

    @Test
    fun rejectsUnsortedUnsupportedOrReservedFieldRecords() {
        val fixture = Fixture()
        assertInvalid(fixture, fixture.snapshot().also { it.putU16(140, 1) })
        assertInvalid(fixture, fixture.snapshot().also { it[142] = 9 })
        assertInvalid(fixture, fixture.snapshot().also { it[143] = 0x4 })
        assertInvalid(fixture, fixture.snapshot().also {
            it[142] = NegotiatedPeerFieldKind.I32.wireCode.toByte()
            it[143] = 0x1
        })
    }

    @Test
    fun rejectsClosedAuthorityAndDoesNotExposeAnAppTrustFactory() {
        val fixture = Fixture()
        val context = fixture.issue(fixture.snapshot())
        fixture.lease.invalidate()

        assertThrows(IllegalStateException::class.java) { context.requireActive() }
        val contextConstructors =
            ManagedResultSchemaContext::class.java.declaredConstructors
        assertTrue(contextConstructors.filterNot { it.isSynthetic }
            .all { Modifier.isPrivate(it.modifiers) })
        assertTrue(contextConstructors.filter { Modifier.isPublic(it.modifiers) }
            .all { it.isSynthetic })
        assertTrue(
            ManagedResultSchemaContext.Companion::class.java.methods.none {
                it.name.startsWith("issue") && !it.isSynthetic
            }
        )
        listOf(
            ManagedResultSchemaExpectation::class.java,
            ManagedResultSchemaLease::class.java
        ).forEach { authorityType ->
            assertTrue(authorityType.declaredConstructors
                .filterNot { it.isSynthetic }
                .all { Modifier.isPrivate(it.modifiers) })
            assertTrue(authorityType.declaredConstructors
                .filter { Modifier.isPublic(it.modifiers) }
                .all { it.isSynthetic })
            assertTrue(authorityType.declaredClasses
                .first { it.simpleName == "Companion" }
                .methods.filter { it.name.startsWith("create") }
                .all { it.isSynthetic })
        }
        assertFalse(
            ManagedResultSchemaContext::class.java.methods.any {
                it.name == "getEndpointToken" || it.name == "getSnapshot"
            }
        )
    }

    @Test
    fun acceptsMaximumSizedDescriptorAndRejectsOversizeOrWrongFieldCount() {
        val fields = (1..64).map {
            NegotiatedPeerField.issue(
                it,
                NegotiatedPeerFieldKind.I32,
                false,
                it % 2 == 0
            )
        }
        val fixture = Fixture(fields = fields)
        assertEquals(392, fixture.snapshot().size)
        assertEquals(64, fixture.issue(fixture.snapshot()).fields.size)
        assertInvalid(fixture, fixture.snapshot() + byteArrayOf(0))
        assertInvalid(fixture, fixture.snapshot().also { it.putU16(68, 63) })
    }

    private fun assertInvalid(fixture: Fixture, snapshot: ByteArray) {
        assertThrows(IllegalArgumentException::class.java) {
            fixture.issue(snapshot)
        }
    }

    private class Fixture(
        fields: List<NegotiatedPeerField> = listOf(
            NegotiatedPeerField.issue(1, NegotiatedPeerFieldKind.UTF8, true, false),
            NegotiatedPeerField.issue(4, NegotiatedPeerFieldKind.I32, false, true),
            NegotiatedPeerField.issue(9, NegotiatedPeerFieldKind.BOOL, false, false)
        )
    ) {
        val lease = ManagedResultSchemaLease.create(
            ENDPOINT_TOKEN,
            PROFILE_ID,
            SERVICE_ID,
            METHOD_ID,
            RESULT_DTO_ID,
            RESULT_DTO_VERSION
        )
        private val expected = ManagedResultSchemaExpectation.create(
            ENDPOINT_TOKEN,
            PROFILE_ID,
            1,
            5,
            2,
            3,
            REQUIRED_FEATURES,
            REQUIRED_FEATURES,
            SERVICE_ID,
            METHOD_ID,
            RESULT_DTO_ID,
            RESULT_DTO_VERSION,
            LOCAL_HASH,
            PEER_HASH,
            fields,
            lease
        )

        fun snapshot(): ByteArray {
            val output = ByteBuffer.allocate(136 + 4 * expected.fields.size)
                .order(ByteOrder.LITTLE_ENDIAN)
            output.put("NLMR".toByteArray(Charsets.US_ASCII))
            output.putShort(1)
            output.putShort(136)
            output.putInt(output.capacity())
            output.putInt(1)
            output.putLong(ENDPOINT_TOKEN)
            output.putInt(PROFILE_ID.toInt())
            output.putInt(1)
            output.putInt(5)
            output.putInt(2)
            output.putInt(3)
            output.putInt(REQUIRED_FEATURES.toInt())
            output.putInt(REQUIRED_FEATURES.toInt())
            output.putInt(SERVICE_ID.toInt())
            output.putInt(METHOD_ID.toInt())
            output.putInt(RESULT_DTO_ID.toInt())
            output.putInt(RESULT_DTO_VERSION.toInt())
            output.putShort(expected.fields.size.toShort())
            output.putShort(4)
            output.put(LOCAL_HASH.hexToBytes())
            output.put(PEER_HASH.hexToBytes())
            for (field in expected.fields) {
                output.putShort(field.id.toShort())
                output.put(field.kind.wireCode.toByte())
                output.put(
                    ((if (field.nullable) 0x1 else 0) or
                        (if (field.optional) 0x2 else 0)).toByte()
                )
            }
            return output.array()
        }

        fun issue(snapshot: ByteArray): ManagedResultSchemaContext =
            ManagedResultSchemaContext.issue(snapshot, expected)
    }

    private companion object {
        const val ENDPOINT_TOKEN = 77L
        const val PROFILE_ID = 1L
        const val SERVICE_ID = 410L
        const val METHOD_ID = 7L
        const val RESULT_DTO_ID = 12L
        const val RESULT_DTO_VERSION = 2L
        const val REQUIRED_FEATURES = 0x36L
        val LOCAL_HASH = "a".repeat(64)
        val PEER_HASH = "b".repeat(64)
    }
}

private fun ByteArray.putU16(offset: Int, value: Int) {
    ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putShort(offset, value.toShort())
}

private fun ByteArray.putU32(offset: Int, value: Long) {
    ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putInt(offset, value.toInt())
}

private fun ByteArray.putU64(offset: Int, value: Long) {
    ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putLong(offset, value)
}
