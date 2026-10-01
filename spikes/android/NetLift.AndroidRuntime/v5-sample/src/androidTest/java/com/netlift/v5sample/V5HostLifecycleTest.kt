package com.netlift.v5sample

import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidhost.contract.NetLiftV5DtoCodec
import com.netlift.androidruntime.NativeAbiException
import com.netlift.androidruntime.v5.CompatibleStartRequest
import com.netlift.androidruntime.v5.NetLiftV5Endpoint
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class V5HostLifecycleTest {
    @Test
    fun managedImagePinMismatchFailsBeforeCompatibleStart() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val endpoint = NetLiftV5Endpoint.open(context)
        try {
            expectIllegalArgument {
                endpoint.start("00".repeat(32))
            }
            assertEquals(null, endpoint.peerContext)
            val dispatchFailure = expectNativeFailure {
                endpoint.dispatch(preStartProbePayload())
            }
            assertEquals(NOT_STARTED, dispatchFailure.statusCode)
        } finally {
            endpoint.close()
        }
    }

    @Test
    fun successfulStartBindsManifestAndDispatchesAcrossRealJni() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val endpoint = NetLiftV5Endpoint.open(context)
        try {
            assertEquals(null, endpoint.peerContext)
            val beforeStart = expectNativeFailure {
                endpoint.dispatch(preStartProbePayload())
            }
            assertEquals(NOT_STARTED, beforeStart.statusCode)

            val preRouteCounters = endpoint.routeCounters()
            assertEquals(0L, preRouteCounters.factoryCalls)
            assertEquals(0L, preRouteCounters.routeCalls)
            assertEquals(0L, preRouteCounters.lastQuantity)
            assertEquals(false, preRouteCounters.optionalDefaultsValid)
            Log.i("NetLiftV5HostTest", "pre-route counters=$preRouteCounters")

            val peer = endpoint.start(V5BuildIdentity.expectedManagedSchemaHashHex)
            assertNotNull(endpoint.peerContext)
            assertTrue(peer.localSchemaHashHex.matches(Regex("[0-9a-f]{64}")))
            assertTrue(peer.schemaHashHex.matches(Regex("[0-9a-f]{64}")))
            assertEquals(
                V5BuildIdentity.expectedManagedSchemaHashHex,
                peer.localSchemaHashHex
            )
            assertEquals(
                V5BuildIdentity.nativePeerSchemaHashHex,
                peer.schemaHashHex
            )
            assertEquals(NetLiftV5DtoCodec.schemaHash, peer.schemaHashHex)
            assertNotEquals(peer.localSchemaHashHex, peer.schemaHashHex)
            assertEquals(
                if (V5BuildIdentity.nativePeerIncludesOptionalFields) 5 else 2,
                peer.fields.size
            )
            assertEquals(peer, endpoint.peerContext)

            val payload = V5SampleCodec.encode(peer)
            val routeValue = endpoint.dispatch(peer, payload)
            assertEquals(7, routeValue)
            val counters = endpoint.routeCounters()
            assertTrue(counters.factoryCalls > 0)
            assertTrue(counters.routeCalls > 0)
            assertEquals(7L, counters.lastQuantity)
            assertEquals(
                !V5BuildIdentity.nativePeerIncludesOptionalFields,
                counters.optionalDefaultsValid
            )

            endpoint.close()
            expectIllegalState { peer.requireActive() }
            expectIllegalState { endpoint.dispatch(payload) }
        } finally {
            endpoint.close()
        }
    }

    @Test
    fun failedCompatibleStartIsOneShotAndNeverIssuesContext() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val endpoint = NetLiftV5Endpoint.open(context)
        try {
            val countersBeforeFailure = endpoint.routeCounters()
            val config = readHostConfig(context)
            val optedOutRequest = CompatibleStartRequest(
                peerManifestJson = config.getString("peerManifestJson"),
                trustedPeerHashHex = config.getString("trustedPeerHashHex"),
                trustedMinimumMajor = config.getInt("trustedMinimumMajor"),
                trustedMinimumMinor = config.getInt("trustedMinimumMinor"),
                peerOptIn = false
            )
            val firstFailure = expectNativeFailure {
                endpoint.start(
                    optedOutRequest,
                    V5BuildIdentity.expectedManagedSchemaHashHex
                )
            }
            assertEquals(OPTED_OUT, firstFailure.statusCode)
            assertEquals(null, endpoint.peerContext)
            assertEquals(countersBeforeFailure, endpoint.routeCounters())

            val secondFailure = expectNativeFailure {
                endpoint.start(V5BuildIdentity.expectedManagedSchemaHashHex)
            }
            assertEquals(OPTED_OUT, secondFailure.statusCode)
            assertEquals(null, endpoint.peerContext)
            assertEquals(countersBeforeFailure, endpoint.routeCounters())

            val dispatchFailure = expectNativeFailure {
                endpoint.dispatch(preStartProbePayload())
            }
            assertEquals(NOT_STARTED, dispatchFailure.statusCode)
        } finally {
            endpoint.close()
        }
    }

    private fun readHostConfig(context: android.content.Context): JSONObject {
        val path = "netlift/apps/${context.packageName}/v5-host.json"
        return context.assets.open(path).bufferedReader().use { JSONObject(it.readText()) }
    }

    private fun preStartProbePayload(): ByteArray = byteArrayOf(
        0x4e,
        0x4c,
        0x46,
        0x31,
        1,
        0,
        0,
        0,
        0,
        0,
        0,
        0
    )

    private fun expectNativeFailure(action: () -> Unit): NativeAbiException {
        try {
            action()
        } catch (failure: NativeAbiException) {
            return failure
        }
        throw AssertionError("Expected a managed v5 ABI failure.")
    }

    private fun expectIllegalState(action: () -> Unit) {
        try {
            action()
        } catch (_: IllegalStateException) {
            return
        }
        throw AssertionError("Expected the operation to fail after endpoint close.")
    }

    private fun expectIllegalArgument(action: () -> Unit) {
        try {
            action()
        } catch (_: IllegalArgumentException) {
            return
        }
        throw AssertionError("Expected a managed-image pin mismatch.")
    }

    private companion object {
        const val NOT_STARTED = 9
        const val OPTED_OUT = 6
    }
}
