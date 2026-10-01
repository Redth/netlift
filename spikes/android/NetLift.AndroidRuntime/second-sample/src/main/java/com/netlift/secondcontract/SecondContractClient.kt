package com.netlift.secondcontract

import android.content.Context
import android.util.Log
import com.netlift.androidhost.contract.DispatchReceipt
import com.netlift.androidhost.contract.DispatchRequest
import com.netlift.androidhost.contract.NetLiftFlatDtoWire
import com.netlift.androidruntime.LiftContract
import com.netlift.androidruntime.NativeAbiException
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.NetLiftTypedReply
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import kotlin.coroutines.AbstractCoroutineContextElement
import kotlin.coroutines.CoroutineContext

internal class SecondContractClient(
    private val contract: LiftContract,
    callbackScope: CoroutineScope,
    private val approve: suspend (String) -> Boolean,
    private val abi: SecondContractNativeAbi,
    private val onError: (Exception) -> Unit = { failure ->
        Log.e("NetLiftSecondApp", failure.message ?: "Second contract callback failed.", failure)
    },
    private val observer: SecondContractClientObserver? = null
) : NetLiftNativeCallbacksV2 {
    private val gate = Any()
    private val pending = mutableMapOf<Long, CancellableContinuation<DispatchReceipt>>()
    private val requests = mutableMapOf<Long, RequestState>()
    private val callbackJob = SupervisorJob(callbackScope.coroutineContext[Job])
    private val ownedCallbackScope = CoroutineScope(
        callbackScope.coroutineContext + callbackJob + CallbackContext()
    )
    private val releaseCompletion = kotlinx.coroutines.CompletableDeferred<Unit>()
    private var registrationToken = 0L
    private var registrationGeneration = 0L
    private var registrationReady = false
    private var closing = false
    private var revokeStarted = false
    private var revoking = false
    private var drainAckStarted = false
    private var released = false
    private var activeRequestCallbacks = 0
    private var activeReplyCalls = 0

    private enum class RequestPhase { OPEN, REPLY_CLAIMED, REVOKED }

    private class CallbackContext : AbstractCoroutineContextElement(Key) {
        companion object Key : CoroutineContext.Key<CallbackContext>
    }

    private class RequestState {
        var job: Job? = null
        var phase = RequestPhase.OPEN
        var jobFinished = false
        var replyFinished = false
    }

    internal data class DiagnosticSnapshot(
        val pendingOperations: Int,
        val callbackRequests: Int,
        val activeRequestCallbacks: Int,
        val activeReplyCalls: Int,
        val callbackJobs: Int,
        val released: Boolean
    )

    init {
        val registration = abi.registerCallbacksV2(contract.confirmationServiceId, this)
        synchronized(gate) {
            require(registration.token != 0L && registration.generation != 0L) {
                "Managed host returned an invalid V2 callback registration."
            }
            check(
                (registrationToken == 0L || registrationToken == registration.token) &&
                    (registrationGeneration == 0L || registrationGeneration == registration.generation)
            ) {
                "V2 callback identity changed while registration was completing."
            }
            registrationToken = registration.token
            registrationGeneration = registration.generation
            registrationReady = true
        }
        maybeAckDrain()
    }

    suspend fun dispatch(request: DispatchRequest): DispatchReceipt =
        suspendCancellableCoroutine { continuation ->
            var operationToken = 0L
            var inserted = false
            var duplicateOperationToken = false
            try {
                val currentRegistration = synchronized(gate) {
                    check(!closing && !released) { "Second contract client is closed." }
                    check(registrationToken != 0L) { "Second contract registration is unavailable." }
                    registrationToken
                }
                operationToken = abi.begin(
                    contract.ordersServiceId,
                    contract.placeOrderMethodId,
                    currentRegistration,
                    NetLiftFlatDtoWire.encodeDispatchRequest(request)
                )
                check(operationToken != 0L) { "Managed host returned an empty operation token." }
                synchronized(gate) {
                    check(!closing && !released) { "Second contract client is closed." }
                    if (pending.containsKey(operationToken)) {
                        duplicateOperationToken = true
                        error("Managed host reused a live operation token.")
                    }
                    pending[operationToken] = continuation
                }
                inserted = true
                continuation.invokeOnCancellation { cancelPending(operationToken) }
                if (!continuation.isActive)
                    return@suspendCancellableCoroutine
                abi.activate(operationToken)
            } catch (failure: Exception) {
                if (inserted) {
                    val owned = synchronized(gate) { pending.remove(operationToken) }
                    if (owned != null) {
                        cancelNative(operationToken)
                        resumeFailure(owned, failure)
                    }
                } else {
                    if (operationToken != 0L && !duplicateOperationToken)
                        cancelNative(operationToken)
                    resumeFailure(continuation, failure)
                }
            }
        }

    internal fun diagnosticIdentity(): Pair<Long, Long> = synchronized(gate) {
        registrationToken to registrationGeneration
    }

    internal fun diagnosticSnapshot(): DiagnosticSnapshot = synchronized(gate) {
        DiagnosticSnapshot(
            pendingOperations = pending.size,
            callbackRequests = requests.size,
            activeRequestCallbacks = activeRequestCallbacks,
            activeReplyCalls = activeReplyCalls,
            callbackJobs = callbackJob.children.count(),
            released = released
        )
    }

    fun requestClose() {
        val token = synchronized(gate) {
            closing = true
            if (released || revokeStarted || revoking) 0L
            else {
                check(registrationToken != 0L) {
                    "Cannot revoke before V2 callback registration completes."
                }
                revokeStarted = true
                registrationToken
            }
        }
        if (token == 0L) {
            maybeAckDrain()
            return
        }
        try {
            abi.revokeV2(token)
        } catch (failure: Exception) {
            synchronized(gate) {
                if (!revoking && !released)
                    revokeStarted = false
            }
            throw failure
        }
    }

    /** Await only from an external owner; callbacks being drained must call requestClose(). */
    suspend fun awaitClosed() {
        check(currentCoroutineContext()[CallbackContext] == null) {
            "Callback handlers must call requestClose() and leave awaiting to an external owner."
        }
        releaseCompletion.await()
    }

    override fun onRequest(
        registrationToken: Long,
        requestToken: Long,
        methodId: Int,
        payload: ByteArray
    ) {
        if (registrationToken == 0L || requestToken == 0L) {
            report(IllegalStateException("Managed host returned an empty callback token."))
            return
        }
        val copiedPayload = payload.copyOf()
        val state = RequestState()
        val admitted = synchronized(gate) {
            if (this.registrationToken != 0L && registrationToken != this.registrationToken ||
                closing || revoking || released || requests.containsKey(requestToken)) {
                false
            } else {
                if (this.registrationToken == 0L)
                    this.registrationToken = registrationToken
                activeRequestCallbacks++
                requests[requestToken] = state
                true
            }
        }
        if (!admitted) {
            if (!synchronized(gate) { closing || revoking || released })
                report(IllegalStateException("Unexpected, duplicate, or stale request token $requestToken."))
            return
        }
        observer?.onRequest(registrationToken, requestToken)

        try {
            val immediateReply = if (methodId != contract.confirmationMethodId) {
                NetLiftTypedReply(
                    outcome = ReplyUnknownMethod,
                    diagnostic = "Unknown approval method.".toByteArray(StandardCharsets.UTF_8)
                )
            } else {
                val prompt = try {
                    SecondContractWire.decodeApprovalPrompt(copiedPayload)
                } catch (failure: Exception) {
                    report(failure)
                    null
                }
                if (prompt == null) {
                    NetLiftTypedReply(
                        outcome = ReplyMalformedPayload,
                        diagnostic = "Malformed approval prompt.".toByteArray(StandardCharsets.UTF_8)
                    )
                } else {
                    try {
                        launchApproval(registrationToken, requestToken, state, prompt)
                        null
                    } catch (failure: Exception) {
                        report(failure)
                        NetLiftTypedReply(
                            outcome = ReplyHandlerFailure,
                            diagnostic = diagnostic(failure)
                        )
                    }
                }
            }
            if (immediateReply != null)
                finishRequest(registrationToken, requestToken, state, immediateReply)
        } finally {
            synchronized(gate) {
                activeRequestCallbacks--
                if (state.phase == RequestPhase.REVOKED && state.job == null)
                    requests.remove(requestToken, state)
            }
            maybeAckDrain()
        }
    }

    private fun launchApproval(
        registrationToken: Long,
        requestToken: Long,
        state: RequestState,
        prompt: String
    ) {
        val job = ownedCallbackScope.launch(start = CoroutineStart.LAZY) {
            val reply = try {
                NetLiftTypedReply(
                    outcome = ReplyResult,
                    accepted = approve(prompt)
                )
            } catch (_: CancellationException) {
                NetLiftTypedReply(outcome = ReplyCancelled)
            } catch (failure: Exception) {
                report(failure)
                NetLiftTypedReply(
                    outcome = ReplyHandlerFailure,
                    diagnostic = diagnostic(failure)
                )
            }
            finishRequest(registrationToken, requestToken, state, reply)
        }
        job.invokeOnCompletion { cause ->
            val completionReply = synchronized(gate) {
                state.jobFinished = true
                if (state.phase == RequestPhase.REVOKED || state.replyFinished) {
                    if (requests[requestToken] === state)
                        requests.remove(requestToken)
                    null
                } else {
                    if (closing || revoking || released || state.phase != RequestPhase.OPEN ||
                        requests[requestToken] !== state) {
                        null
                    } else if (cause is CancellationException) {
                        NetLiftTypedReply(outcome = ReplyCancelled)
                    } else {
                        val failure = cause
                            ?: IllegalStateException("Approval callback completed without a reply.")
                        NetLiftTypedReply(
                            outcome = ReplyHandlerFailure,
                            diagnostic = diagnostic(failure)
                        )
                    }
                }
            }
            if (completionReply != null) {
                when {
                    cause == null ->
                        report(IllegalStateException("Approval callback completed without a reply."))
                    cause !is CancellationException ->
                        report(IllegalStateException("Approval callback job failed.", cause))
                }
                finishRequest(registrationToken, requestToken, state, completionReply)
            }
            maybeAckDrain()
        }
        val startJob = synchronized(gate) {
            if (requests[requestToken] !== state)
                false
            else {
                state.job = job
                !closing && !revoking && !released && state.phase == RequestPhase.OPEN
            }
        }
        if (startJob)
            job.start()
        else
            job.cancel()
    }

    private fun finishRequest(
        registrationToken: Long,
        requestToken: Long,
        state: RequestState,
        reply: NetLiftTypedReply
    ) {
        val claimed = synchronized(gate) {
            if (closing || revoking || released || state.phase != RequestPhase.OPEN ||
                requests[requestToken] !== state) {
                false
            } else {
                state.phase = RequestPhase.REPLY_CLAIMED
                activeReplyCalls++
                true
            }
        }
        if (!claimed)
            return
        var replyFailure: Exception? = null
        try {
            observer?.onReplyClaimed(registrationToken, requestToken)
            observer?.onReplyCallStarted(registrationToken, requestToken)
            abi.replyTyped(registrationToken, requestToken, reply)
        } catch (failure: Exception) {
            replyFailure = failure
            report(failure)
        } finally {
            observer?.onReplyReturned(registrationToken, requestToken, replyFailure)
            synchronized(gate) {
                state.replyFinished = true
                if (state.job == null || state.jobFinished)
                    requests.remove(requestToken, state)
                activeReplyCalls--
            }
            maybeAckDrain()
        }
    }

    override fun onResult(
        operationToken: Long,
        status: Int,
        result: ByteArray,
        error: ByteArray
    ) {
        val continuation = synchronized(gate) { pending.remove(operationToken) }
        if (continuation == null) {
            report(IllegalStateException("Late or duplicate result for operation $operationToken."))
            return
        }
        val outcome: Result<DispatchReceipt> = try {
            when {
                status == StatusCancelled ->
                    Result.failure(CancellationException("Managed dispatch was cancelled."))
                status != 0 && result.isNotEmpty() ->
                    Result.failure(IllegalStateException(
                        "Managed failure status $status included a result payload."
                    ))
                status != 0 ->
                    Result.failure(decodeDispatchFailure(status, error.copyOf()))
                error.isNotEmpty() ->
                    Result.failure(IllegalStateException(
                        "Successful managed dispatch included an error diagnostic."
                    ))
                else ->
                    Result.success(NetLiftFlatDtoWire.decodeDispatchReceipt(result.copyOf()))
            }
        } catch (failure: Exception) {
            Result.failure(failure)
        }
        continuation.resumeWith(outcome)
    }

    override fun onRelease(registrationToken: Long) {
        val release = synchronized(gate) {
            if (registrationToken != this.registrationToken || released)
                null
            else {
                val violation = if (!revoking || !drainAckStarted ||
                    activeRequestCallbacks != 0 || activeReplyCalls != 0 || requests.isNotEmpty()) {
                    IllegalStateException("Native release arrived before the V2 callback drain completed.")
                } else {
                    null
                }
                released = true
                closing = true
                val operations = pending.values.toList()
                pending.clear()
                violation to operations
            }
        }
        if (release == null) {
            report(IllegalStateException("Unexpected or duplicate V2 release token $registrationToken."))
            return
        }
        observer?.onRelease(registrationToken)
        callbackJob.cancel()
        val (violation, operations) = release
        val closedFailure = violation ?: IllegalStateException("Native registration was released.")
        operations.forEach { resumeFailure(it, closedFailure) }
        if (violation == null)
            releaseCompletion.complete(Unit)
        else {
            report(violation)
            releaseCompletion.completeExceptionally(violation)
        }
    }

    override fun onRevoke(registrationToken: Long, generation: Long) {
        val jobs = synchronized(gate) {
            if (registrationToken == 0L || generation == 0L || revoking || released ||
                (this.registrationToken != 0L && registrationToken != this.registrationToken) ||
                (registrationGeneration != 0L && generation != registrationGeneration)) {
                null
            } else {
                this.registrationToken = registrationToken
                registrationGeneration = generation
                closing = true
                revoking = true
                requests.values.filter { it.phase == RequestPhase.OPEN }.mapNotNull {
                    it.phase = RequestPhase.REVOKED
                    it.job
                }
            }
        }
        if (jobs == null) {
            report(IllegalStateException("Unexpected or duplicate V2 revoke $registrationToken/$generation."))
            return
        }
        observer?.onRevoke(registrationToken, generation)
        jobs.forEach { it.cancel() }
        maybeAckDrain()
    }

    private fun maybeAckDrain() {
        val identity = synchronized(gate) {
            if (!registrationReady || !revoking || released || drainAckStarted ||
                activeRequestCallbacks != 0 || activeReplyCalls != 0 || requests.isNotEmpty()) {
                null
            } else {
                drainAckStarted = true
                registrationToken to registrationGeneration
            }
        } ?: return
        try {
            observer?.onDrainAck(identity.first, identity.second)
            abi.ackDrain(identity.first, identity.second)
        } catch (failure: Exception) {
            releaseCompletion.completeExceptionally(failure)
            report(failure)
        }
    }

    private fun cancelPending(operationToken: Long) {
        val continuation = synchronized(gate) { pending.remove(operationToken) }
        if (continuation != null)
            cancelNative(operationToken)
    }

    private fun cancelNative(operationToken: Long) {
        try {
            abi.cancel(operationToken)
        } catch (failure: Exception) {
            report(failure)
        }
    }

    private fun resumeFailure(
        continuation: CancellableContinuation<DispatchReceipt>,
        failure: Exception
    ) {
        continuation.resumeWith(Result.failure(failure))
    }

    private fun decodeDispatchFailure(status: Int, diagnostic: ByteArray): Exception {
        val message = try {
            StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(diagnostic))
                .toString()
        } catch (failure: Exception) {
            return IllegalStateException(
                "Managed dispatch failed with status $status and an invalid UTF-8 diagnostic.",
                failure
            )
        }
        return IllegalStateException("Managed dispatch failed with status $status: $message")
    }

    private fun diagnostic(failure: Throwable): ByteArray {
        val fallback = "Approval handler failed.".toByteArray(StandardCharsets.US_ASCII)
        val bytes = (failure.message ?: failure.javaClass.simpleName)
            .toByteArray(StandardCharsets.UTF_8)
        return if (bytes.size <= MaxDiagnosticBytes) bytes else fallback
    }

    private fun report(failure: Exception) = onError(failure)

    companion object {
        private const val ReplyResult = 0
        private const val ReplyCancelled = 1
        private const val ReplyMalformedPayload = 3
        private const val ReplyUnknownMethod = 4
        private const val ReplyHandlerFailure = 5
        private const val StatusCancelled = 12
        private const val MaxDiagnosticBytes = 1024

        suspend fun create(
            context: Context,
            callbackScope: CoroutineScope,
            observer: SecondContractClientObserver? = null,
            onError: (Exception) -> Unit = { failure ->
                Log.e("NetLiftSecondApp", failure.message ?: "Second contract callback failed.", failure)
            },
            approve: suspend (String) -> Boolean
        ): SecondContractClient {
            val contract = NetLiftHost.initialize(context.applicationContext)
            val wrongHash = contract.schemaHash.copyOf().also {
                it[0] = (it[0].toInt() xor 1).toByte()
            }
            val mismatch = try {
                NetLiftHost.start(context.applicationContext, contract.abiVersion, wrongHash)
                null
            } catch (failure: NativeAbiException) {
                failure
            }
            check(mismatch?.statusCode == 5) {
                "Wrong schema hash did not fail with SCHEMA_MISMATCH (5)."
            }
            val runtime = NetLiftHost.start(
                context.applicationContext,
                contract.abiVersion,
                contract.schemaHash
            )
            check(runtime.schemaHash.contentEquals(contract.schemaHash)) {
                "Runtime returned a different app contract schema hash."
            }
            check(runtime.featureBits and contract.requiredFeatureBits == contract.requiredFeatureBits) {
                "Generic AAR runtime is missing a required app contract feature."
            }
            return SecondContractClient(
                contract,
                callbackScope,
                approve,
                NetLiftSecondContractNativeAbi,
                onError,
                observer
            )
        }
    }
}

internal object SecondContractWire {
    private const val MaxBytes = 4096

    fun decodeApprovalPrompt(payload: ByteArray): String {
        val reader = Reader(payload)
        val prompt = reader.string(1)
        reader.requireEnd()
        return prompt
    }

    fun encodeApprovalPrompt(prompt: String): ByteArray {
        val output = ByteArrayOutputStream()
        writeField(output, 1, prompt.toByteArray(StandardCharsets.UTF_8))
        return output.toByteArray()
    }

    private fun writeField(output: ByteArrayOutputStream, id: Int, value: ByteArray) {
        require(id in 1..0xffff) { "Wire field ID is out of range." }
        require(value.size <= MaxBytes - output.size() - 6) {
            "Wire payload exceeds the ABI limit."
        }
        val header = ByteBuffer.allocate(6)
            .order(ByteOrder.LITTLE_ENDIAN)
            .putShort(id.toShort())
            .putInt(value.size)
            .array()
        output.write(header)
        output.write(value)
    }

    private class Reader(private val source: ByteArray) {
        private var offset = 0

        init {
            require(source.size <= MaxBytes) { "Wire payload exceeds the ABI limit." }
        }

        fun string(id: Int): String = StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(field(id)))
            .toString()

        fun requireEnd() {
            require(offset == source.size) { "Trailing wire payload bytes." }
        }

        private fun field(expectedId: Int): ByteArray {
            require(source.size - offset >= 6) { "Truncated wire field header." }
            val header = ByteBuffer.wrap(source, offset, 6).order(ByteOrder.LITTLE_ENDIAN)
            val fieldId = header.short.toInt() and 0xffff
            val length = header.int
            offset += 6
            require(fieldId == expectedId) { "Unexpected, duplicate, or reordered wire field." }
            require(length >= 0 && length <= MaxBytes && length <= source.size - offset) {
                "Invalid wire field length."
            }
            return source.copyOfRange(offset, offset + length).also { offset += length }
        }
    }
}
