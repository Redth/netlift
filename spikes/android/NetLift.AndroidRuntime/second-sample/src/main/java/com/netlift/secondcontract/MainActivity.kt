package com.netlift.secondcontract

import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.netlift.androidhost.contract.DispatchClient
import com.netlift.androidhost.contract.DispatchRequest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.util.concurrent.atomic.AtomicReference

class MainActivity : ComponentActivity() {
    private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val pendingApproval = AtomicReference<CompletableDeferred<Boolean>?>()
    private var activeClient: SecondContractClient? = null
    private var activeGeneratedClient: DispatchClient? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            var status by remember { mutableStateOf("Starting independent managed contract…") }
            var result by remember { mutableStateOf("No dispatch yet.") }
            var channel by remember { mutableStateOf("north-star") }
            var sequence by remember { mutableStateOf("73") }
            var client by remember { mutableStateOf<SecondContractClient?>(null) }
            var generatedClient by remember { mutableStateOf<DispatchClient?>(null) }
            var inFlight by remember { mutableStateOf(false) }
            var approval by remember { mutableStateOf<PendingApproval?>(null) }
            val approvalHandler: suspend (String) -> Boolean = { prompt ->
                val completion = CompletableDeferred<Boolean>()
                pendingApproval.set(completion)
                withContext(Dispatchers.Main.immediate) {
                    approval = PendingApproval(prompt, completion)
                }
                try {
                    completion.await()
                } finally {
                    pendingApproval.compareAndSet(completion, null)
                    withContext(Dispatchers.Main.immediate) {
                        if (approval?.completion === completion)
                            approval = null
                    }
                }
            }

            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier.fillMaxSize().padding(24.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        Text("Second native-owned app / independent Dispatch schema")
                        Text(status)
                        Text(result)
                        OutlinedTextField(
                            value = channel,
                            onValueChange = { channel = it },
                            label = { Text("Channel") },
                            modifier = Modifier.fillMaxWidth()
                        )
                        OutlinedTextField(
                            value = sequence,
                            onValueChange = { sequence = it },
                            label = { Text("Sequence") },
                            modifier = Modifier.fillMaxWidth()
                        )
                        Button(
                            enabled = client != null && !inFlight,
                            onClick = {
                                val currentClient = client ?: return@Button
                                val parsedSequence = sequence.toLongOrNull()
                                if (parsedSequence == null) {
                                    status = "Sequence must be an integer."
                                    return@Button
                                }
                                inFlight = true
                                activityScope.launch {
                                    try {
                                        val receipt = currentClient.dispatch(
                                            DispatchRequest(channel, parsedSequence)
                                        )
                                        result = "Generated DispatchReceipt: ${receipt.channel}, " +
                                            "sequence ${receipt.sequence}, delivery ${receipt.deliveryId}, " +
                                            "approved ${receipt.approved}"
                                        status = "Distinct managed Dispatch contract completed."
                                    } catch (_: CancellationException) {
                                        status = "Dispatch cancelled."
                                    } catch (failure: Exception) {
                                        status = "Dispatch failed: ${failure.message}"
                                    } finally {
                                        inFlight = false
                                    }
                                }
                            }
                        ) {
                            Text("Dispatch")
                        }
                        Button(
                            enabled = generatedClient != null && !inFlight,
                            onClick = {
                                val currentClient = generatedClient ?: return@Button
                                val parsedSequence = sequence.toLongOrNull()
                                if (parsedSequence == null) {
                                    status = "Sequence must be an integer."
                                    return@Button
                                }
                                inFlight = true
                                activityScope.launch {
                                    try {
                                        val receipt = currentClient.dispatchAsync(
                                            DispatchRequest(channel, parsedSequence)
                                        )
                                        result = "Generated facade DispatchReceipt: ${receipt.channel}, " +
                                            "sequence ${receipt.sequence}, delivery ${receipt.deliveryId}, " +
                                            "approved ${receipt.approved}"
                                        status = "Generated Kotlin V2 facade completed."
                                    } catch (_: CancellationException) {
                                        status = "Generated dispatch cancelled."
                                    } catch (failure: Exception) {
                                        status = "Generated dispatch failed: ${failure.message}"
                                    } finally {
                                        inFlight = false
                                    }
                                }
                            }
                        ) {
                            Text("Dispatch via generated facade")
                        }
                        Button(
                            enabled = client != null && !inFlight,
                            onClick = {
                                inFlight = true
                                result = "Real JNI lifecycle stress is running."
                                activityScope.launch {
                                    try {
                                        val summary = SecondContractJniStressRunner.run(
                                            applicationContext
                                        ) { progress ->
                                            withContext(Dispatchers.Main.immediate) {
                                                status = progress
                                            }
                                        }
                                        status = "Real JNI V2 stress passed in " +
                                            "${summary.elapsedMillis}ms."
                                        result = "cycles=${summary.cycles}; requests=${summary.requestCallbacks}; " +
                                            "typedReplies=${summary.replyCalls}; " +
                                            "generationReuses=${summary.generationReuseTransitions}; " +
                                            "revoke/ack/release=${summary.revokeCallbacks}/" +
                                            "${summary.drainAcks}/${summary.releases}; " +
                                            "pending=${summary.pendingRequestRevokes}; " +
                                            "claimedReplyRaces=${summary.claimedReplyRevokes}; " +
                                            "replyErrorsAfterRevoke=${summary.replyErrorsAfterRevoke}; " +
                                            "staleAck/duplicateAck/duplicateRevoke/postReleaseBegin=" +
                                            "${summary.staleAckRejections}/${summary.duplicateAckRejections}/" +
                                            "${summary.duplicateRevokeRejections}/" +
                                            "${summary.postReleaseBeginRejections}; " +
                                            "reentrantIdleAcks=${summary.reentrantIdleAcks}; PID=${summary.pid}; " +
                                            "syntheticStale/Duplicate=" +
                                            "${summary.injectedStaleCallbacks}/" +
                                            "${summary.injectedDuplicateCallbacks}; " +
                                            "PSS=${summary.pssBeforeKb}->${summary.pssAfterKb}KB"
                                    } catch (failure: Exception) {
                                        Log.e("NetLiftSecondApp", "Real JNI V2 stress failed.", failure)
                                        status = "Real JNI V2 stress failed: ${failure.message}"
                                    } finally {
                                        inFlight = false
                                    }
                                }
                            }
                        ) {
                            Text("Run real JNI V2 stress")
                        }
                    }
                }
            }

            approval?.let { pending ->
                AlertDialog(
                    onDismissRequest = { pending.completion.complete(false) },
                    title = { Text("Managed-origin typed approval") },
                    text = { Text(pending.prompt) },
                    confirmButton = {
                        TextButton(onClick = { pending.completion.complete(true) }) {
                            Text("Approve")
                        }
                    },
                    dismissButton = {
                        TextButton(onClick = { pending.completion.complete(false) }) {
                            Text("Decline")
                        }
                    }
                )
            }

            LaunchedEffect(Unit) {
                try {
                    val created = withContext(Dispatchers.IO) {
                        SecondContractClient.create(
                            applicationContext, callbackScope, approve = approvalHandler)
                    }
                    activeClient = created
                    client = created
                    val generated = withContext(Dispatchers.IO) {
                        DispatchClient(
                            GeneratedSecondContractAbi(applicationContext),
                            callbackScope,
                            approvalHandler,
                            onError = { failure ->
                                Log.e("NetLiftSecondApp", "Generated facade callback failed.", failure)
                            }
                        )
                    }
                    activeGeneratedClient = generated
                    generatedClient = generated
                    val contract = withContext(Dispatchers.IO) {
                        com.netlift.androidruntime.NetLiftHost.initialize(applicationContext)
                    }
                    status = "CoreCLR ready; wrong schema rejected with status 5; " +
                        "generated schema ${contract.schemaHash.toHex().take(12)}"
                } catch (failure: Exception) {
                    status = "Second contract startup failed: ${failure.message}"
                }
            }
        }
    }

    override fun onDestroy() {
        pendingApproval.getAndSet(null)?.complete(false)
        val client = activeClient
        activeClient = null
        val generatedClient = activeGeneratedClient
        activeGeneratedClient = null
        if (client != null) {
            val closeRequested = try {
                client.requestClose()
                true
            } catch (failure: Exception) {
                Log.e("NetLiftSecondApp", "Could not revoke the second-contract callback.", failure)
                false
            }
            if (closeRequested) {
                releaseScope.launch(Dispatchers.IO) {
                    try {
                        withTimeout(5_000) { client.awaitClosed() }
                    } catch (failure: Exception) {
                        Log.e("NetLiftSecondApp", "Second-contract callback drain did not close.", failure)
                    }
                }
            }
        }
        if (generatedClient != null) {
            val closeRequested = try {
                generatedClient.requestClose()
                true
            } catch (failure: Exception) {
                Log.e("NetLiftSecondApp", "Could not revoke the generated callback.", failure)
                false
            }
            if (closeRequested) {
                releaseScope.launch(Dispatchers.IO) {
                    try {
                        withTimeout(5_000) { generatedClient.awaitClosed() }
                    } catch (failure: Exception) {
                        Log.e("NetLiftSecondApp", "Generated callback drain did not close.", failure)
                    }
                }
            }
        }
        activityScope.cancel()
        super.onDestroy()
    }

    private data class PendingApproval(
        val prompt: String,
        val completion: CompletableDeferred<Boolean>
    )

    private fun ByteArray.toHex(): String =
        joinToString(separator = "") { "%02x".format(it.toInt() and 0xff) }

    private companion object {
        val callbackScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val releaseScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }
}
