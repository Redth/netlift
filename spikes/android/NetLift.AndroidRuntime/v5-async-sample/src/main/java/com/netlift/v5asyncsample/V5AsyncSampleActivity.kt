package com.netlift.v5asyncsample

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.v5.ManagedResultSchemaContext
import com.netlift.androidruntime.v5.NegotiatedPeerContext
import com.netlift.androidruntime.v5.NetLiftV5AsyncSession
import com.netlift.androidruntime.v5.NetLiftV5Endpoint
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout

internal data class AsyncSampleUiState(
    val message: String = "Starting the compatible endpoint…",
    val pendingRequestToken: Long? = null,
    val confirmationText: String = "",
    val activeOperationToken: Long? = null
)

internal fun acknowledgeReplyIfOperationActive(
    state: AsyncSampleUiState,
    operationToken: Long,
    requestToken: Long,
    accepted: Boolean
): AsyncSampleUiState {
    if (state.activeOperationToken != operationToken ||
        state.pendingRequestToken != requestToken)
        return state

    return state.copy(
        message = if (accepted) "Approval replied." else "Decline replied.",
        pendingRequestToken = null,
        confirmationText = ""
    )
}

internal fun completeOperationIfActive(
    state: AsyncSampleUiState,
    operationToken: Long,
    message: String
): AsyncSampleUiState {
    if (state.activeOperationToken != operationToken)
        return state

    return state.copy(
        message = message,
        pendingRequestToken = null,
        confirmationText = "",
        activeOperationToken = null
    )
}

internal fun updateOperationMessageIfActive(
    state: AsyncSampleUiState,
    operationToken: Long,
    message: String
): AsyncSampleUiState {
    if (state.activeOperationToken != operationToken)
        return state

    return state.copy(message = message)
}

internal fun updateRequestMessageIfActive(
    state: AsyncSampleUiState,
    operationToken: Long,
    requestToken: Long,
    message: String
): AsyncSampleUiState {
    if (state.activeOperationToken != operationToken ||
        state.pendingRequestToken != requestToken)
        return state

    return state.copy(message = message)
}

internal data class V5AsyncSampleDecodedResult(
    val code: Int,
    val accepted: Boolean,
    val audited: Boolean?
)

public class V5AsyncSampleActivity : ComponentActivity() {
    private lateinit var model: AsyncSampleModel

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        model = (lastCustomNonConfigurationInstance as? AsyncSampleModel)
            ?: AsyncSampleModel(applicationContext)
        setContent {
            val state by model.state.collectAsState()
            MaterialTheme {
                Scaffold { insets ->
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(insets)
                            .padding(24.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp)
                    ) {
                        Text("NetLift async-callback-v1 / V2")
                        Text(state.message)
                        Button(
                            enabled = state.activeOperationToken == null,
                            onClick = model::submit
                        ) {
                            Text("Submit managed task")
                        }
                        Button(
                            enabled = state.activeOperationToken != null,
                            onClick = model::cancel
                        ) {
                            Text("Cancel managed task")
                        }
                        state.pendingRequestToken?.let {
                            AlertDialog(
                                onDismissRequest = { model.reply(false) },
                                title = { Text("Managed ConfirmAsync") },
                                text = { Text(state.confirmationText) },
                                confirmButton = {
                                    TextButton(onClick = { model.reply(true) }) {
                                        Text("Approve")
                                    }
                                },
                                dismissButton = {
                                    TextButton(onClick = { model.reply(false) }) {
                                        Text("Decline")
                                    }
                                }
                            )
                        }
                    }
                }
            }
        }
    }

    override fun onRetainCustomNonConfigurationInstance(): Any = model

    override fun onDestroy() {
        if (::model.isInitialized && isFinishing)
            model.shutdown()
        super.onDestroy()
    }
}

private class AsyncSampleModel(applicationContext: android.content.Context) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val stateValue = MutableStateFlow(AsyncSampleUiState())
    val state: StateFlow<AsyncSampleUiState> = stateValue.asStateFlow()
    private val shutdownStarted = AtomicBoolean(false)
    private val revokeReceived = CompletableDeferred<Unit>()
    private val releaseReceived = CompletableDeferred<Unit>()
    private var endpoint: NetLiftV5Endpoint? = null
    private var session: NetLiftV5AsyncSession? = null
    private var peerContext: NegotiatedPeerContext? = null
    private var resultContext: ManagedResultSchemaContext? = null

    init {
        scope.launch { initialize(applicationContext) }
    }

    fun submit() {
        scope.launch {
            try {
                val activeSession = checkNotNull(session)
                val peer = checkNotNull(peerContext)
                val payload = V5AsyncSampleCodec.encode(peer, "approve", 7)
                val operation = activeSession.begin(payload)
                stateValue.update {
                    it.copy(
                        message = "Managed Task started; waiting for ConfirmAsync.",
                        activeOperationToken = operation
                    )
                }
                activeSession.activate(operation)
            } catch (failure: Exception) {
                showFailure("Could not start the managed task", failure)
            }
        }
    }

    fun cancel() {
        scope.launch {
            val operation = stateValue.value.activeOperationToken ?: return@launch
            try {
                val activeSession = checkNotNull(session) {
                    "The async session is not available."
                }
                activeSession.cancel(operation)
                stateValue.update { current ->
                    updateOperationMessageIfActive(
                        current,
                        operation,
                        "Cancellation requested; awaiting terminal result."
                    )
                }
            } catch (failure: Exception) {
                val message = "Could not cancel the managed task: " +
                    (failure.message ?: failure.javaClass.simpleName)
                stateValue.update { current ->
                    updateOperationMessageIfActive(current, operation, message)
                }
            }
        }
    }

    fun reply(accepted: Boolean) {
        scope.launch {
            val stateBeforeReply = stateValue.value
            val request = stateBeforeReply.pendingRequestToken ?: return@launch
            val operation = stateBeforeReply.activeOperationToken ?: return@launch
            try {
                val activeSession = checkNotNull(session) {
                    "The async session is not available."
                }
                activeSession.replyConfirmation(request, accepted)
                stateValue.update { current ->
                    acknowledgeReplyIfOperationActive(current, operation, request, accepted)
                }
            } catch (failure: Exception) {
                val message = "Could not reply to ConfirmAsync: " +
                    (failure.message ?: failure.javaClass.simpleName)
                stateValue.update { current ->
                    updateRequestMessageIfActive(current, operation, request, message)
                }
            }
        }
    }

    fun shutdown() {
        if (!shutdownStarted.compareAndSet(false, true))
            return
        scope.launch {
            try {
                val activeSession = session
                val activeEndpoint = endpoint
                if (activeSession != null) {
                    activeSession.revoke()
                    withTimeout(LIFECYCLE_TIMEOUT_MILLIS) { revokeReceived.await() }
                    activeSession.acknowledgeDrain()
                    withTimeout(LIFECYCLE_TIMEOUT_MILLIS) { releaseReceived.await() }
                    activeSession.close()
                }
                activeEndpoint?.close()
                val leases = session?.leaseSnapshot()
                if (leases != null)
                    check(leases.allIndexed { index, value ->
                        index == RELEASE_COUNT_INDEX || value == 0L
                    }) { "Native V2 shutdown left outstanding leases: ${leases.joinToString()}." }
            } catch (failure: Exception) {
                showFailure("AsyncRuntime V2 shutdown failed", failure)
            } finally {
                scope.cancel()
            }
        }
    }

    private fun initialize(applicationContext: android.content.Context) {
        try {
            val opened = NetLiftV5Endpoint.open(applicationContext)
            endpoint = opened
            val peer = opened.start(V5AsyncBuildIdentity.expectedManagedSchemaHashHex)
            peerContext = peer
            resultContext = checkNotNull(opened.managedResultSchemaContext)
            session = opened.openAsyncV2Session(object : NetLiftNativeCallbacksV2 {
                override fun onRequest(
                    registrationToken: Long,
                    requestToken: Long,
                    methodId: Int,
                    payload: ByteArray
                ) {
                    try {
                        check(registrationToken == session?.registration?.token)
                        check(methodId == CONFIRM_METHOD_ID)
                        stateValue.update {
                            it.copy(
                                message = "Managed ConfirmAsync is waiting for a typed reply.",
                                pendingRequestToken = requestToken,
                                confirmationText = decodeConfirmationText(payload)
                            )
                        }
                    } catch (failure: Exception) {
                        showFailure("Invalid managed confirmation request", failure)
                    }
                }

                override fun onResult(
                    operationToken: Long,
                    status: Int,
                    result: ByteArray,
                    error: ByteArray
                ) {
                    if (stateValue.value.activeOperationToken != operationToken)
                        return
                    if (status == STATUS_OK) {
                        try {
                            val decoded = V5AsyncSampleCodec.decode(
                                result,
                                checkNotNull(resultContext)
                            )
                            val message = "Task completed: code=${decoded.code}, " +
                                "accepted=${decoded.accepted}, " +
                                "audited=${decoded.audited ?: "not present in peer codec"}"
                            stateValue.update { current ->
                                completeOperationIfActive(current, operationToken, message)
                            }
                        } catch (failure: Exception) {
                            showFailure("Generated result decoding failed", failure)
                        }
                    } else {
                        val message = if (status == STATUS_CANCELLED) {
                            "Managed Task completed as cancelled (not as a reply)."
                        } else {
                            "Managed Task failed with status $status."
                        }
                        stateValue.update { current ->
                            completeOperationIfActive(current, operationToken, message)
                        }
                    }
                }

                override fun onRevoke(registrationToken: Long, generation: Long) {
                    if (registrationToken == session?.registration?.token &&
                        generation == session?.registration?.generation)
                        revokeReceived.complete(Unit)
                }

                override fun onRelease(registrationToken: Long) {
                    if (registrationToken == session?.registration?.token)
                        releaseReceived.complete(Unit)
                }
            })
            stateValue.update { it.copy(message = "V2 active; submit to run the managed Task.") }
        } catch (failure: Exception) {
            showFailure("AsyncRuntime V2 startup failed", failure)
        }
    }

    private fun showFailure(operation: String, failure: Exception) {
        stateValue.update {
            it.copy(message = "$operation: ${failure.message ?: failure.javaClass.simpleName}")
        }
    }

    private fun decodeConfirmationText(source: ByteArray): String {
        require(source.size >= NLF1_HEADER_BYTES + FIELD_HEADER_BYTES) {
            "ConfirmAsync payload is truncated."
        }
        val input = ByteBuffer.wrap(source).order(ByteOrder.LITTLE_ENDIAN)
        require(input.get() == 'N'.code.toByte() && input.get() == 'L'.code.toByte() &&
            input.get() == 'F'.code.toByte() && input.get() == '1'.code.toByte() &&
            (input.short.toInt() and 0xffff) == 1
        ) { "ConfirmAsync payload header is invalid." }
        val fieldCount = input.short.toInt() and 0xffff
        val bodyLength = input.int.toLong() and UINT32_MAX
        require(fieldCount == 1 && bodyLength == source.size - NLF1_HEADER_BYTES.toLong()) {
            "ConfirmAsync payload field table is invalid."
        }
        val fieldId = input.short.toInt() and 0xffff
        val kind = input.get().toInt() and 0xff
        val fieldLength = input.int.toLong() and UINT32_MAX
        require(fieldId == CONFIRM_TEXT_FIELD_ID && kind == UTF8_KIND &&
            fieldLength == input.remaining().toLong()
        ) { "ConfirmAsync payload field is unsupported." }
        val bytes = ByteArray(input.remaining())
        input.get(bytes)
        return StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    }

    private companion object {
        const val CONFIRM_METHOD_ID = 1
        const val CONFIRM_TEXT_FIELD_ID = 1
        const val UTF8_KIND = 1
        const val NLF1_HEADER_BYTES = 12
        const val FIELD_HEADER_BYTES = 7
        const val STATUS_OK = 0
        const val STATUS_CANCELLED = 12
        const val UINT32_MAX = 0xffff_ffffL
        const val RELEASE_COUNT_INDEX = 3
        const val LIFECYCLE_TIMEOUT_MILLIS = 15_000L

        fun LongArray.allIndexed(predicate: (Int, Long) -> Boolean): Boolean =
            indices.all { index -> predicate(index, this[index]) }
    }
}
