package com.netlift.secondcontract

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidhost.contract.DispatchClient
import com.netlift.androidhost.contract.DispatchRequest
import com.netlift.androidhost.contract.LiftAbiException
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeFailureHooks
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class GeneratedDispatchClientIntegrationTest {
    @Test
    fun generatedFacadeDispatchesThroughRealCoreClrAndDrainsBothOutcomes() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val abi = GeneratedSecondContractAbi(context)
        val baseline = requireNotNull(NetLiftNativeFailureHooks.ownershipSnapshot())
        val errors = AtomicReference<Exception?>()
        val callbackScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

        val approving = DispatchClient(abi, callbackScope, { true }, errors::set)
        try {
            val receipt = withTimeout(5_000) {
                approving.dispatchAsync(DispatchRequest("generated-accept", 73L))
            }
            assertEquals("delivered:generated-accept", receipt.channel)
            assertEquals(73L, receipt.sequence)
            assertTrue(receipt.approved)
        } finally {
            approving.requestClose()
            withTimeout(5_000) { approving.awaitClosed() }
        }
        assertOwnershipReturned(baseline)

        val declining = DispatchClient(abi, callbackScope, { false }, errors::set)
        try {
            val failure = try {
                withTimeout(5_000) {
                    declining.dispatchAsync(DispatchRequest("generated-decline", 74L))
                }
                null
            } catch (error: LiftAbiException) {
                error
            }
            assertEquals(8, failure?.statusCode)
        } finally {
            declining.requestClose()
            withTimeout(5_000) { declining.awaitClosed() }
        }
        assertOwnershipReturned(baseline)
        assertNull(errors.get())
    }

    @Test
    fun generatedFacadeCancelsPendingApprovalAndDrainsOnClose() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        NetLiftHost.initialize(context)
        val baseline = requireNotNull(NetLiftNativeFailureHooks.ownershipSnapshot())
        val callbackScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val pendingApproval = CompletableDeferred<Boolean>()
        val entered = CountDownLatch(1)
        val errors = AtomicReference<Exception?>()
        val client = DispatchClient(
            GeneratedSecondContractAbi(context),
            callbackScope,
            {
                entered.countDown()
                pendingApproval.await()
            },
            errors::set
        )
        val operation = callbackScope.async {
            client.dispatchAsync(DispatchRequest("generated-close", 75L))
        }
        try {
            assertTrue("Managed approval callback was not invoked.",
                entered.await(5, TimeUnit.SECONDS))
            client.requestClose()
            withTimeout(5_000) { client.awaitClosed() }
            val failure = try {
                withTimeout(5_000) { operation.await() }
                null
            } catch (error: Exception) {
                error
            }
            assertTrue("Expected registration revocation, got $failure.",
                failure is LiftAbiException && failure.statusCode == 11)
        } finally {
            client.requestClose()
            pendingApproval.cancel()
            operation.cancel()
            withTimeout(5_000) { client.awaitClosed() }
        }
        assertOwnershipReturned(baseline)
        assertNull(errors.get())
    }

    private fun assertOwnershipReturned(baseline: LongArray) {
        val current = requireNotNull(NetLiftNativeFailureHooks.ownershipSnapshot())
        assertEquals(baseline[CALLBACK_CONTEXTS_LIVE], current[CALLBACK_CONTEXTS_LIVE])
        assertEquals(baseline[CALLBACK_REFS_LIVE], current[CALLBACK_REFS_LIVE])
        assertEquals(baseline[REGISTERED_CALLBACKS], current[REGISTERED_CALLBACKS])
        assertEquals(baseline[PENDING_CALLBACK_RELEASES], current[PENDING_CALLBACK_RELEASES])
    }

    private companion object {
        const val CALLBACK_CONTEXTS_LIVE = 3
        const val CALLBACK_REFS_LIVE = 7
        const val REGISTERED_CALLBACKS = 17
        const val PENDING_CALLBACK_RELEASES = 19
    }
}
