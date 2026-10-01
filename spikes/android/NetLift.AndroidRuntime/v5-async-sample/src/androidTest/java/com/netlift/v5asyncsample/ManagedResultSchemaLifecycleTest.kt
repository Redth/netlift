package com.netlift.v5asyncsample

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidhost.contract.NetLiftV5AsyncCodec
import com.netlift.androidruntime.v5.NegotiatedPeerFieldKind
import com.netlift.androidruntime.v5.NetLiftV5Endpoint
import java.lang.reflect.Method
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ManagedResultSchemaLifecycleTest {
    @Test
    fun liveCoreClrEndpointIssuesNlmRAndDecodesDeterministicResultVector() {
        val appContext = InstrumentationRegistry.getInstrumentation().targetContext
        val endpoint = NetLiftV5Endpoint.open(appContext)
        val token = endpointToken(endpoint)
        try {
            assertNull(endpoint.managedResultSchemaContext)
            val preStart = ByteArray(SHORT_BUFFER_SIZE) { BUFFER_SENTINEL }
            assertNativeSchemaCall(
                invokeManagedResultSchema(token, 17, 1, preStart),
                NOT_STARTED,
                0,
                0
            )
            assertTrue(preStart.all { it == BUFFER_SENTINEL })

            val peer = endpoint.start(V5AsyncBuildIdentity.expectedManagedSchemaHashHex)
            val resultContext = endpoint.managedResultSchemaContext
            assertNotNull(resultContext)
            resultContext!!.requireActive()
            assertEquals(V5AsyncBuildIdentity.expectedManagedSchemaHashHex,
                resultContext.localSchemaHashHex)
            assertEquals(V5AsyncBuildIdentity.nativePeerSchemaHashHex,
                resultContext.peerSchemaHashHex)
            assertEquals(NetLiftV5AsyncCodec.schemaHash, peer.schemaHashHex)
            assertEquals(1L, resultContext.profileId)
            assertEquals(1L, resultContext.abiMajor)
            assertEquals(5L, resultContext.schemaVersion)
            assertEquals(1L, resultContext.contractMajor)
            assertEquals(
                if (V5AsyncBuildIdentity.localResultHasOptionalField) 1L else 0L,
                resultContext.contractMinor
            )
            assertEquals(0x36L, resultContext.featureBits)
            assertEquals(0x36L, resultContext.requiredFeatureBits)
            assertEquals(17L, resultContext.serviceId)
            assertEquals(1L, resultContext.methodId)
            assertEquals(22L, resultContext.resultDtoId)
            assertEquals(1L, resultContext.resultDtoVersion)
            assertEquals(
                if (V5AsyncBuildIdentity.localResultHasOptionalField) {
                    listOf(1, 2, 9)
                } else {
                    listOf(1, 2)
                },
                resultContext.fields.map { it.id }
            )
            assertTrue(resultContext.fields.all { !it.nullable })
            assertEquals(
                V5AsyncBuildIdentity.localResultHasOptionalField,
                resultContext.fields.last().optional
            )
            assertEquals(NegotiatedPeerFieldKind.I32, resultContext.fields.first().kind)
            assertEquals(NegotiatedPeerFieldKind.BOOL, resultContext.fields[1].kind)

            val wrongRouteBuffer = ByteArray(SHORT_BUFFER_SIZE) { BUFFER_SENTINEL }
            assertNativeSchemaCall(
                invokeManagedResultSchema(token, 17, 2, wrongRouteBuffer),
                INVALID_ARGUMENT,
                0,
                0
            )
            assertTrue(wrongRouteBuffer.all { it == BUFFER_SENTINEL })

            val shortBuffer = ByteArray(SHORT_BUFFER_SIZE) { BUFFER_SENTINEL }
            val shortResult = invokeManagedResultSchema(token, 17, 1, shortBuffer)
            val requiredBytes = if (V5AsyncBuildIdentity.localResultHasOptionalField) 148 else 144
            assertNativeSchemaCall(
                shortResult,
                SCHEMA_BUFFER_TOO_SMALL,
                0,
                requiredBytes
            )
            assertTrue(shortBuffer.all { it == BUFFER_SENTINEL })

            val exactBuffer = ByteArray(requiredBytes)
            assertNativeSchemaCall(
                invokeManagedResultSchema(token, 17, 1, exactBuffer),
                OK,
                requiredBytes,
                requiredBytes
            )
            assertEquals("NLMR", String(exactBuffer, 0, 4, StandardCharsets.US_ASCII))

            val includesOptionalField = resultContext.fields.any { it.id == 9 }
            val vector = resultVector(includeAudited = includesOptionalField)
            val decoded = NetLiftV5AsyncCodec.decodeResult(vector, resultContext)
            assertEquals(203, invokeGetter(decoded, "getCode"))
            assertEquals(true, invokeGetter(decoded, "getAccepted"))
            val auditedGetter = decoded.javaClass.methods.singleOrNull {
                it.name == "getAudited" && it.parameterCount == 0
            }
            if (auditedGetter != null) {
                assertFalse(invokeGetter(decoded, "getAudited") as Boolean)
            } else {
                assertTrue(includesOptionalField)
            }

            endpoint.close()
            expectIllegalState { resultContext.requireActive() }
            val closedBuffer = ByteArray(SHORT_BUFFER_SIZE) { BUFFER_SENTINEL }
            assertNativeSchemaCall(
                invokeManagedResultSchema(token, 17, 1, closedBuffer),
                INVALID_HANDLE,
                0,
                0
            )
            assertTrue(closedBuffer.all { it == BUFFER_SENTINEL })
            expectIllegalState { NetLiftV5AsyncCodec.decodeResult(vector, resultContext) }
        } finally {
            endpoint.close()
        }
    }

    private fun resultVector(includeAudited: Boolean): ByteArray {
        val fields = buildList {
            add(NlfField(1, 2, ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(203).array()))
            add(NlfField(2, 3, byteArrayOf(1)))
            if (includeAudited)
                add(NlfField(9, 3, byteArrayOf(1)))
        }
        val bodySize = fields.sumOf { FIELD_HEADER_SIZE + it.value.size }
        val output = ByteBuffer.allocate(NLF1_HEADER_SIZE + bodySize)
            .order(ByteOrder.LITTLE_ENDIAN)
        output.put("NLF1".toByteArray(StandardCharsets.US_ASCII))
        output.putShort(1)
        output.putShort(fields.size.toShort())
        output.putInt(bodySize)
        for (field in fields) {
            output.putShort(field.id.toShort())
            output.put(field.kind.toByte())
            output.putInt(field.value.size)
            output.put(field.value)
        }
        return output.array()
    }

    private fun endpointToken(endpoint: NetLiftV5Endpoint): Long {
        val field = NetLiftV5Endpoint::class.java.getDeclaredField("endpointToken")
        field.isAccessible = true
        return field.getLong(endpoint)
    }

    private fun invokeManagedResultSchema(
        endpointToken: Long,
        serviceId: Int,
        methodId: Int,
        destination: ByteArray
    ): LongArray {
        val apiClass = Class.forName("com.netlift.androidruntime.v5.NativeV5Api")
        val apiInstance = apiClass.getDeclaredField("INSTANCE").let { field ->
            field.isAccessible = true
            field.get(null)
        }
        val method: Method = apiClass.declaredMethods.single {
            it.name.contains("nativeGetManagedResultSchema") &&
                it.parameterTypes.contentEquals(
                    arrayOf(
                        java.lang.Long.TYPE,
                        Integer.TYPE,
                        Integer.TYPE,
                        ByteArray::class.java
                    )
                )
        }
        method.isAccessible = true
        return method.invoke(
            apiInstance,
            endpointToken,
            serviceId,
            methodId,
            destination
        ) as LongArray
    }

    private fun assertNativeSchemaCall(
        counts: LongArray,
        status: Int,
        bytesWritten: Int,
        requiredBytes: Int
    ) {
        assertEquals(3, counts.size)
        assertEquals(status.toLong(), counts[0])
        assertEquals(bytesWritten.toLong(), counts[1])
        assertEquals(requiredBytes.toLong(), counts[2])
    }

    private fun invokeGetter(value: Any, name: String): Any? =
        value.javaClass.getMethod(name).invoke(value)

    private fun expectIllegalState(action: () -> Unit) {
        try {
            action()
        } catch (_: IllegalStateException) {
            return
        }
        throw AssertionError("Expected managed-result context use to fail after close.")
    }

    private data class NlfField(val id: Int, val kind: Int, val value: ByteArray)

    private companion object {
        const val NLF1_HEADER_SIZE = 12
        const val FIELD_HEADER_SIZE = 7
        const val SHORT_BUFFER_SIZE = 136
        const val BUFFER_SENTINEL = 0x5a.toByte()
        const val OK = 0
        const val INVALID_ARGUMENT = 1
        const val NOT_STARTED = 9
        const val INVALID_HANDLE = 10
        const val SCHEMA_BUFFER_TOO_SMALL = 12
    }
}
