package com.netlift.sample

import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
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
import com.netlift.androidhost.contract.LiftAbiException
import com.netlift.androidhost.contract.LiftStreamException
import com.netlift.androidhost.contract.OrderDraft
import com.netlift.androidhost.contract.OrdersClient
import com.netlift.androidhost.contract.NativeConfirmationRequest
import com.netlift.androidhost.contract.watchAsyncStream
import com.netlift.androidruntime.NetLiftHost
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class MainActivity : ComponentActivity() {
    private var activeOrdersClient: OrdersClient? = null
    private var activityStreamScope: CoroutineScope? = null
    private var streamCollectionJob: Job? = null
    private val activityClosing = AtomicBoolean(false)
    private val autoConfirmRemaining = AtomicInteger()
    private val slowStreamItems = AtomicBoolean()
    private val streamReceivedFirstItem = AtomicBoolean()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        activityClosing.set(false)
        autoConfirmRemaining.set(0)
        slowStreamItems.set(false)
        streamReceivedFirstItem.set(false)
        val streamScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        activityStreamScope = streamScope
        setContent {
            var status by remember { mutableStateOf("Starting .NET 11 CoreCLR…") }
            var latestOrderUpdate by remember { mutableStateOf("Waiting for Orders.Business updates.") }
            var ordersClient by remember { mutableStateOf<OrdersClient?>(null) }
            var sku by remember { mutableStateOf("NETLIFT-ANDROID-東京-🙂") }
            var quantity by remember { mutableStateOf("2") }
            var currentOrderJob by remember { mutableStateOf<Job?>(null) }
            var streamStressJob by remember { mutableStateOf<Job?>(null) }
            var lifecycleStressJob by remember { mutableStateOf<Job?>(null) }
            val pendingPrompt = remember { mutableStateOf<PendingPrompt?>(null) }
            val failNextConfirmation = remember { AtomicBoolean(false) }
            val mainHandler = remember { Handler(Looper.getMainLooper()) }

            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(24.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        Text("Typed-v4 Orders / callback lifecycle-v2 / real business service")
                        Text(status)
                        Text("WatchAsync: $latestOrderUpdate")
                        OutlinedTextField(
                            value = sku,
                            onValueChange = { sku = it },
                            label = { Text("SKU") },
                            modifier = Modifier.fillMaxWidth()
                        )
                        OutlinedTextField(
                            value = quantity,
                            onValueChange = { quantity = it },
                            label = { Text("Quantity") },
                            modifier = Modifier.fillMaxWidth()
                        )
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Button(
                                enabled = ordersClient != null && currentOrderJob == null,
                                onClick = {
                                    val client = ordersClient ?: return@Button
                                    val parsedQuantity = quantity.toIntOrNull() ?: 0
                                    status = "Calling real Orders.Business…"
                                    currentOrderJob = processOrderScope.launch {
                                        try {
                                            val receipt = client.placeAsync(OrderDraft(sku, parsedQuantity))
                                            status = "Accepted order ${receipt.id}: ${receipt.sku} x ${receipt.quantity}"
                                        } catch (_: CancellationException) {
                                            status = "Order cancelled (status 12)."
                                        } catch (failure: Exception) {
                                            val code = (failure as? LiftAbiException)?.statusCode
                                            status = when {
                                                code == 8 && failure.message == "Order declined." ->
                                                    "Order declined through typed RESULT(false)."
                                                code == 8 && failure.message == "Order validation failed." ->
                                                    "Real Orders.Business validation rejected the order."
                                                code == null -> "Order failed: ${failure.message}"
                                                else -> "Order failed (status $code): ${failure.message}"
                                            }
                                        } finally {
                                            currentOrderJob = null
                                        }
                                    }
                                }
                            ) {
                                Text("Place order")
                            }
                            Button(
                                enabled = currentOrderJob != null,
                                onClick = { currentOrderJob?.cancel() }
                            ) {
                                Text("Cancel order")
                            }
                        }
                        Button(
                            enabled = ordersClient != null && currentOrderJob == null,
                            onClick = {
                                failNextConfirmation.set(true)
                                status = "Next confirmation will return HANDLER_FAILURE (status 13)."
                            }
                        ) {
                            Text("Inject status 13")
                        }
                        Button(
                            enabled = currentOrderJob == null,
                            onClick = { recreate() }
                        ) {
                            Text("Recreate Activity")
                        }
                        Button(
                            enabled = ordersClient != null &&
                                streamReceivedFirstItem.get() &&
                                streamStressJob == null,
                            onClick = {
                                val client = ordersClient ?: return@Button
                                autoConfirmRemaining.set(34)
                                slowStreamItems.set(true)
                                status = "Sending 34 real orders to exercise WatchAsync backpressure…"
                                streamStressJob = streamScope.launch(Dispatchers.Default) {
                                    try {
                                        coroutineScope {
                                            (1..34).map { index ->
                                                async {
                                                    client.placeAsync(
                                                        OrderDraft("STREAM-BACKPRESSURE-$index", 1)
                                                    )
                                                }
                                            }.awaitAll()
                                        }
                                        withContext(Dispatchers.Main.immediate) {
                                            status = "Stress orders accepted; waiting for WatchAsync terminal."
                                        }
                                    } catch (cancelled: CancellationException) {
                                        throw cancelled
                                    } catch (failure: Exception) {
                                        withContext(Dispatchers.Main.immediate) {
                                            status = "Stream stress failed: ${failure.message}"
                                        }
                                    } finally {
                                        withContext(NonCancellable + Dispatchers.Main.immediate) {
                                            streamStressJob = null
                                        }
                                    }
                                }
                            }
                        ) {
                            Text("Test WatchAsync overflow")
                        }
                        Button(
                            enabled = ordersClient != null && lifecycleStressJob == null,
                            onClick = {
                                status = "Running 10,000 native callback lifecycle-v2 cycles…"
                                lifecycleStressJob = streamScope.launch(Dispatchers.IO) {
                                    try {
                                        AndroidOrdersAbi(applicationContext)
                                            .verifyLifecycleV2Cycles(
                                                com.netlift.androidhost.contract.NetLiftOrdersContract
                                                    .CONFIRMATION_SERVICE_ID,
                                                10_000
                                            )
                                        withContext(Dispatchers.Main.immediate) {
                                            status = "10,000 callback lifecycle-v2 cycles passed."
                                        }
                                    } catch (failure: Exception) {
                                        withContext(Dispatchers.Main.immediate) {
                                            status = "Lifecycle-v2 stress failed: ${failure.message}"
                                        }
                                    } finally {
                                        withContext(NonCancellable + Dispatchers.Main.immediate) {
                                            lifecycleStressJob = null
                                        }
                                    }
                                }
                            }
                        ) {
                            Text("Test lifecycle-v2 x10,000")
                        }
                    }
                }
            }

            pendingPrompt.value?.let { prompt ->
                AlertDialog(
                    onDismissRequest = { prompt.completion.complete(false) },
                    title = { Text("Confirm order") },
                    text = { Text(prompt.request.prompt) },
                    confirmButton = {
                        TextButton(onClick = { prompt.completion.complete(true) }) {
                            Text("Accept")
                        }
                    },
                    dismissButton = {
                        Row {
                            TextButton(onClick = { prompt.completion.complete(false) }) {
                                Text("Decline")
                            }
                            TextButton(
                                onClick = {
                                    prompt.completion.cancel(CancellationException("Confirmation cancelled by user."))
                                }
                            ) {
                                Text("Cancel")
                            }
                            TextButton(onClick = { recreate() }) {
                                Text("Recreate")
                            }
                        }
                    }
                )
            }

            LaunchedEffect(Unit) {
                try {
                    withContext(Dispatchers.IO) {
                        NetLiftHost.initialize(applicationContext)
                    }
                    val createdClient = OrdersClient(
                        abi = AndroidOrdersAbi(applicationContext),
                        callbackScope = processCallbackScope,
                        confirmation = { request: NativeConfirmationRequest ->
                            val autoConfirm = autoConfirmRemaining.getAndUpdate { remaining ->
                                if (remaining > 0) remaining - 1 else 0
                            }
                            if (autoConfirm > 0) {
                                true
                            } else {
                                if (failNextConfirmation.getAndSet(false))
                                    throw IllegalStateException("Injected confirmation handler failure.")
                                val completion = CompletableDeferred<Boolean>()
                                withContext(Dispatchers.Main.immediate) {
                                    pendingPrompt.value = PendingPrompt(request, completion)
                                }
                                try {
                                    completion.await()
                                } finally {
                                    withContext(Dispatchers.Main.immediate) {
                                        if (pendingPrompt.value?.completion === completion)
                                            pendingPrompt.value = null
                                    }
                                }
                            }
                        },
                        onError = { failure ->
                            val expectedDiagnostic =
                                failure.message == "Native confirmation handler was cancelled." ||
                                    (activityClosing.get() &&
                                        (failure.message?.startsWith("Late native confirmation reply") == true ||
                                            failure.message?.startsWith("Late or duplicate native result") == true)) ||
                                    failure.message == "Injected confirmation handler failure."
                            if (expectedDiagnostic) {
                                Log.i("NetLiftTypedSample", "Expected diagnostic: ${failure.message}")
                            } else {
                                Log.e("NetLiftTypedSample", "Generated callback diagnostic", failure)
                                mainHandler.post {
                                    status = "Callback diagnostic: ${failure.message}"
                                }
                            }
                        }
                    )
                    activeOrdersClient = createdClient
                    ordersClient = createdClient
                    status = "CoreCLR ready; ABI v1 / typed-v4 streams / schema " +
                        com.netlift.androidhost.contract.NetLiftOrdersContract.SCHEMA_HASH_HEX.take(12)
                    streamCollectionJob = streamScope.launch {
                        try {
                            watchAsyncStream(AndroidOrdersStreamAbi(applicationContext)).collect { update ->
                                if (slowStreamItems.get())
                                    delay(500)
                                streamReceivedFirstItem.set(true)
                                latestOrderUpdate = "Order ${update.id} ${update.state}"
                            }
                        } catch (cancelled: CancellationException) {
                            throw cancelled
                        } catch (failure: LiftStreamException) {
                            slowStreamItems.set(false)
                            autoConfirmRemaining.set(0)
                            latestOrderUpdate = "FAILED/${failure.status}: ${failure.message}"
                            if (failure.status == 8)
                                status = "Real Orders.Business WatchAsync failed with FAILED/8."
                            else
                                status = "Orders stream failed (${failure.status}): ${failure.message}"
                        } catch (failure: Exception) {
                            latestOrderUpdate = "Stream transport failed: ${failure.message}"
                        }
                    }
                } catch (failure: Exception) {
                    status = "CoreCLR startup failed: ${failure.message}"
                }
            }
        }
    }

    override fun onDestroy() {
        activityClosing.set(true)
        streamCollectionJob?.cancel()
        streamCollectionJob = null
        activityStreamScope?.cancel()
        activityStreamScope = null
        activeOrdersClient?.let { client ->
            activeOrdersClient = null
            releaseScope.launch(start = CoroutineStart.UNDISPATCHED) { client.close() }
        }
        super.onDestroy()
    }

    private data class PendingPrompt(
        val request: NativeConfirmationRequest,
        val completion: CompletableDeferred<Boolean>
    )

    private companion object {
        val processCallbackScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val processOrderScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        val releaseScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }
}
