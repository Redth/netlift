package com.netlift.v5asyncsample

import android.content.pm.ApplicationInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.netlift.androidruntime.NativeAbiException
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.v5.NetLiftV5AsyncSession
import com.netlift.androidruntime.v5.NetLiftV5Endpoint
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AsyncV2ManagedTaskLifecycleTest {
    @Test
    fun pendingManagedTaskSurvivesActivityRecreation() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        assumeTrue(
            "Activity private-field retention check runs only against the debuggable app.",
            context.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0
        )
        val scenario = ActivityScenario.launch(V5AsyncSampleActivity::class.java)
        var scenarioClosed = false
        try {
            val model = awaitActivityModel(scenario) {
                it.message() == "V2 active; submit to run the managed Task."
            }
            invokeModel(model, "submit")
            awaitModelState(model) {
                it.pendingRequestToken() != null
            }

            scenario.recreate()
            val recreatedModel = activityModel(scenario)
            assertSame(model, recreatedModel)
            assertTrue(activityState(recreatedModel).pendingRequestToken() != null)

            invokeModel(recreatedModel, "reply", true)
            awaitModelState(recreatedModel) {
                it.message().startsWith("Task completed:") &&
                    it.message().contains("accepted=true")
            }

            val session = modelField(recreatedModel, "session")
            scenario.close()
            scenarioClosed = true
            awaitCondition({ "V2 shutdown did not drain all leases after Activity close." }) {
                val snapshot = session.javaClass.getMethod("leaseSnapshot")
                    .invoke(session) as LongArray
                snapshot.size == 5 &&
                    snapshot[0] == 0L &&
                    snapshot[1] == 0L &&
                    snapshot[2] == 0L &&
                    snapshot[3] > 0L &&
                    snapshot[4] == 0L
            }
            assertTrue(activityState(recreatedModel).message().startsWith("Task completed:"))
        } finally {
            if (!scenarioClosed)
                scenario.close()
        }
    }

    @Test
    fun generatedManagedTaskCompletesForApprovalAndDecline() {
        val harness = openHarness()
        var failure: Throwable? = null
        try {
            listOf(true, false).forEach { accepted ->
                val operation = harness.session.begin(
                    V5AsyncSampleCodec.encode(
                        harness.peerContext,
                        "approve",
                        7
                    )
                )
                harness.session.activate(operation)

                val request = harness.callbacks.requests.poll(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)
                    ?: throw AssertionError("Managed Task did not call native ConfirmAsync.")
                assertEquals(CONFIRM_METHOD_ID, request.methodId)
                assertTrue(decodeConfirmationText(request.payload).isNotBlank())
                harness.session.replyConfirmation(request.requestToken, accepted)

                val result = harness.callbacks.results.poll(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)
                    ?: throw AssertionError("Managed Task did not publish a terminal result.")
                assertEquals(operation, result.operationToken)
                assertEquals(STATUS_OK, result.status)
                val decoded = V5AsyncSampleCodec.decode(
                    result.result,
                    harness.resultContext
                )
                assertEquals(7, decoded.code)
                assertEquals(accepted, decoded.accepted)
                assertNull(
                    "Managed business operation must invoke ConfirmAsync exactly once.",
                    harness.callbacks.requests.poll(1, TimeUnit.SECONDS)
                )
            }

            finishLifecycle(harness)
            assertZeroLeases(harness.session.leaseSnapshot())
        } catch (error: Throwable) {
            failure = error
            throw error
        } finally {
            closeIfActiveAfterFailure(harness, failure)
        }
    }

    @Test
    fun cancellationProducesCancelledTerminalInsteadOfTypedReply() {
        val harness = openHarness()
        var failure: Throwable? = null
        try {
            val operation = harness.session.begin(
                V5AsyncSampleCodec.encode(
                    harness.peerContext,
                    "NETLIFT-ANDROID-CANCEL",
                    3
                )
            )
            harness.session.activate(operation)
            val request = harness.callbacks.requests.poll(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)
                ?: throw AssertionError("Managed Task did not call native ConfirmAsync.")

            harness.session.cancel(operation)
            val result = harness.callbacks.results.poll(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)
                ?: throw AssertionError("Cancellation did not publish a terminal result.")
            assertEquals(operation, result.operationToken)
            assertEquals(STATUS_CANCELLED, result.status)
            try {
                harness.session.replyConfirmation(request.requestToken, accepted = true)
                throw AssertionError("A cancelled request accepted a late typed reply.")
            } catch (expected: NativeAbiException) {
                assertTrue(expected.statusCode != STATUS_OK)
            }

            finishLifecycle(harness)
            assertZeroLeases(harness.session.leaseSnapshot())
        } catch (error: Throwable) {
            failure = error
            throw error
        } finally {
            closeIfActiveAfterFailure(harness, failure)
        }
    }

    private fun openHarness(): Harness {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val endpoint = NetLiftV5Endpoint.open(context)
        try {
            val peer = endpoint.start(V5AsyncBuildIdentity.expectedManagedSchemaHashHex)
            val resultContext = checkNotNull(endpoint.managedResultSchemaContext)
            val callbacks = RecordingCallbacks()
            val session = endpoint.openAsyncV2Session(callbacks)
            return Harness(endpoint, peer, resultContext, session, callbacks)
        } catch (failure: Exception) {
            endpoint.close()
            throw failure
        }
    }

    private fun awaitActivityModel(
        scenario: ActivityScenario<V5AsyncSampleActivity>,
        condition: (Any) -> Boolean
    ): Any {
        var model: Any? = null
        var lastState: Any? = null
        awaitCondition({
            "Activity did not reach the expected model state. Last state: " +
                (lastState ?: "<unavailable>")
        }) {
            model = activityModel(scenario)
            lastState = activityState(checkNotNull(model))
            condition(checkNotNull(lastState))
        }
        return checkNotNull(model)
    }

    private fun awaitModelState(model: Any, condition: (Any) -> Boolean) {
        var lastState: Any? = null
        awaitCondition({
            "Async sample model did not reach the expected state. Last state: " +
                (lastState ?: "<unavailable>")
        }) {
            lastState = activityState(model)
            condition(checkNotNull(lastState))
        }
    }

    private fun awaitCondition(message: () -> String, condition: () -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(EVENT_TIMEOUT_SECONDS)
        while (System.nanoTime() < deadline) {
            if (condition())
                return
            Thread.sleep(50)
        }
        throw AssertionError(message())
    }

    private fun activityModel(scenario: ActivityScenario<V5AsyncSampleActivity>): Any {
        var model: Any? = null
        scenario.onActivity { activity -> model = modelField(activity, "model") }
        return checkNotNull(model)
    }

    private fun activityState(model: Any): Any {
        val stateFlow = model.javaClass.getMethod("getState").invoke(model)
        return checkNotNull(stateFlow.javaClass.getMethod("getValue").invoke(stateFlow))
    }

    private fun invokeModel(model: Any, method: String, vararg arguments: Any) {
        val parameterTypes = arguments.map {
            if (it is Boolean) java.lang.Boolean.TYPE else it.javaClass
        }.toTypedArray()
        model.javaClass.getMethod(method, *parameterTypes).invoke(model, *arguments)
    }

    private fun modelField(model: Any, name: String): Any {
        val field = model.javaClass.getDeclaredField(name)
        field.isAccessible = true
        return checkNotNull(field.get(model))
    }

    private fun Any.message(): String =
        javaClass.getMethod("getMessage").invoke(this) as String

    private fun Any.pendingRequestToken(): Long? =
        javaClass.getMethod("getPendingRequestToken").invoke(this) as Long?

    private fun finishLifecycle(harness: Harness) {
        harness.session.revoke()
        assertTrue(
            "V2 revoke callback was not delivered.",
            harness.callbacks.revoke.await(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)
        )
        harness.session.acknowledgeDrain()
        assertTrue(
            "V2 release callback was not delivered after drain acknowledgement.",
            harness.callbacks.release.await(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)
        )
        harness.session.close()
        harness.endpoint.close()
        harness.finished = true
    }

    private fun closeIfActive(harness: Harness) {
        if (harness.finished)
            return
        harness.session.revoke()
        check(harness.callbacks.revoke.await(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)) {
            "V2 revoke callback was not delivered during cleanup."
        }
        harness.session.acknowledgeDrain()
        check(harness.callbacks.release.await(EVENT_TIMEOUT_SECONDS, TimeUnit.SECONDS)) {
            "V2 release callback was not delivered during cleanup."
        }
        harness.session.close()
        harness.endpoint.close()
        harness.finished = true
    }

    private fun closeIfActiveAfterFailure(harness: Harness, failure: Throwable?) {
        if (harness.finished)
            return
        try {
            closeIfActive(harness)
        } catch (cleanupFailure: Throwable) {
            if (failure == null)
                throw cleanupFailure
            failure.addSuppressed(cleanupFailure)
        }
    }

    private fun assertZeroLeases(snapshot: LongArray) {
        assertEquals(5, snapshot.size)
        assertEquals(0L, snapshot[0])
        assertEquals(0L, snapshot[1])
        assertEquals(0L, snapshot[2])
        assertTrue("Expected onRelease to run.", snapshot[3] > 0)
        assertEquals(0L, snapshot[4])
    }

    private fun decodeConfirmationText(source: ByteArray): String {
        val input = ByteBuffer.wrap(source).order(ByteOrder.LITTLE_ENDIAN)
        require(source.size >= NLF1_HEADER_BYTES + FIELD_HEADER_BYTES &&
            input.get() == 'N'.code.toByte() && input.get() == 'L'.code.toByte() &&
            input.get() == 'F'.code.toByte() && input.get() == '1'.code.toByte() &&
            (input.short.toInt() and 0xffff) == 1
        ) { "ConfirmAsync request is not a complete NLF1 frame." }
        val fields = input.short.toInt() and 0xffff
        val bodyLength = input.int.toLong() and UINT32_MAX
        val fieldId = input.short.toInt() and 0xffff
        val kind = input.get().toInt() and 0xff
        val length = input.int.toLong() and UINT32_MAX
        require(fields == 1 && fieldId == CONFIRM_TEXT_FIELD_ID && kind == UTF8_KIND &&
            bodyLength == source.size - NLF1_HEADER_BYTES.toLong() &&
            length == input.remaining().toLong()
        ) { "ConfirmAsync request did not contain the expected typed text field." }
        val bytes = ByteArray(input.remaining())
        input.get(bytes)
        return StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    }

    private data class Harness(
        val endpoint: NetLiftV5Endpoint,
        val peerContext: com.netlift.androidruntime.v5.NegotiatedPeerContext,
        val resultContext: com.netlift.androidruntime.v5.ManagedResultSchemaContext,
        val session: NetLiftV5AsyncSession,
        val callbacks: RecordingCallbacks,
        var finished: Boolean = false
    )

    private data class Request(
        val requestToken: Long,
        val methodId: Int,
        val payload: ByteArray
    )

    private data class Result(
        val operationToken: Long,
        val status: Int,
        val result: ByteArray,
        val error: ByteArray
    )

    private class RecordingCallbacks : NetLiftNativeCallbacksV2 {
        val requests = LinkedBlockingQueue<Request>()
        val results = LinkedBlockingQueue<Result>()
        val revoke = java.util.concurrent.CountDownLatch(1)
        val release = java.util.concurrent.CountDownLatch(1)

        override fun onRequest(
            registrationToken: Long,
            requestToken: Long,
            methodId: Int,
            payload: ByteArray
        ) {
            requests.put(Request(requestToken, methodId, payload.copyOf()))
        }

        override fun onResult(
            operationToken: Long,
            status: Int,
            result: ByteArray,
            error: ByteArray
        ) {
            results.put(Result(operationToken, status, result.copyOf(), error.copyOf()))
        }

        override fun onRevoke(registrationToken: Long, generation: Long) {
            revoke.countDown()
        }

        override fun onRelease(registrationToken: Long) {
            release.countDown()
        }
    }

    private companion object {
        const val CONFIRM_METHOD_ID = 1
        const val CONFIRM_TEXT_FIELD_ID = 1
        const val UTF8_KIND = 1
        const val STATUS_OK = 0
        const val STATUS_CANCELLED = 12
        const val NLF1_HEADER_BYTES = 12
        const val FIELD_HEADER_BYTES = 7
        const val UINT32_MAX = 0xffff_ffffL
        const val EVENT_TIMEOUT_SECONDS = 20L
    }
}
