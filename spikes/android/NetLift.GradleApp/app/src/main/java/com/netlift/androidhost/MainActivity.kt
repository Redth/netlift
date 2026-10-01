package com.netlift.androidhost

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.netlift.androidhost.contract.LiftAbiException
import com.netlift.androidhost.contract.NativeConfirmationRequest
import com.netlift.androidhost.contract.OrderDraft
import com.netlift.androidhost.contract.OrderReceipt
import com.netlift.androidhost.contract.OrdersClient
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.io.File
import java.io.IOException
import java.nio.CharBuffer
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.resume

class MainActivity : ComponentActivity() {
    private var status by mutableStateOf("Preparing CoreCLR...")
    private var callbackStatus by mutableStateOf("No managed callback yet")
    private var confirmationPrompt by mutableStateOf<String?>(null)
    private var confirmationContinuation: CancellableContinuation<Boolean>? = null
    private var ordersClient: OrdersClient? = null
    private val activityJob = SupervisorJob()
    private val activityScope = CoroutineScope(activityJob + Dispatchers.Main.immediate)

    external fun nativeInitialize(filesDirUtf8: ByteArray, nativeLibraryDirUtf8: ByteArray): Int
    external fun nativeGetRuntimeVersionMajor(): Int

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(24.dp),
                        verticalArrangement = Arrangement.Center,
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Text("NetLift Android CoreCLR")
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(status)
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(callbackStatus)
                        Spacer(modifier = Modifier.height(16.dp))
                        Button(
                            onClick = {
                                status = "Calling managed runtime probe..."
                                managedExecutor.execute {
                                    val versionMajor = nativeGetRuntimeVersionMajor()
                                    runOnUiThread {
                                        if (!isDestroyed) {
                                            status = if (versionMajor >= 0) {
                                                "Managed runtime probe returned .NET $versionMajor"
                                            } else {
                                                "Managed runtime probe failed ($versionMajor); see logcat"
                                            }
                                        }
                                    }
                                }
                            },
                            enabled = status.startsWith("CoreCLR ready") ||
                                status.startsWith("Managed runtime probe returned") ||
                                status.startsWith("Managed call returned") ||
                                status == "Order declined in Kotlin"
                        ) {
                            Text("Probe managed runtime")
                        }
                        Spacer(modifier = Modifier.height(16.dp))
                        Button(
                            onClick = ::placeManagedOrder,
                            enabled = status.startsWith("CoreCLR ready") ||
                                status.startsWith("Managed runtime probe returned") ||
                                status.startsWith("Managed call returned") ||
                                status == "Order declined in Kotlin"
                        ) {
                            Text("Place managed order")
                        }
                    }
                }
                confirmationPrompt?.let { prompt ->
                    AlertDialog(
                        onDismissRequest = { resolveConfirmation(false) },
                        title = { Text("Confirm managed order") },
                        text = { Text(prompt) },
                        confirmButton = {
                            Button(onClick = { resolveConfirmation(true) }) {
                                Text("Accept")
                            }
                        },
                        dismissButton = {
                            Button(onClick = { resolveConfirmation(false) }) {
                                Text("Decline")
                            }
                        }
                    )
                }
            }
        }

        managedExecutor.execute {
            try {
                if (!managedAssetsInstalled) {
                    installManagedAssets()
                    managedAssetsInstalled = true
                }
            } catch (exception: IOException) {
                android.util.Log.e("NetLiftCoreCLR", "Failed to install managed assets", exception)
                runOnUiThread {
                    if (!isDestroyed) {
                        status = "Managed assets could not be installed; see logcat"
                    }
                }
                return@execute
            }
            val result = nativeInitialize(
                strictUtf8Bytes(filesDir.absolutePath),
                strictUtf8Bytes(applicationInfo.nativeLibraryDir)
            )
            runOnUiThread {
                if (!isDestroyed) {
                    if (result == 0) {
                        initializeOrdersClient()
                    } else {
                        status = "CoreCLR initialization failed ($result); see logcat"
                    }
                }
            }
        }
    }

    override fun onDestroy() {
        resolveConfirmation(false)
        val client = ordersClient
        ordersClient = null
        if (client != null) {
            processScope.launch {
                try {
                    client.close()
                } catch (exception: Exception) {
                    android.util.Log.e("NetLiftCoreCLR", "Failed to close generated Orders client", exception)
                }
            }
        }
        activityScope.cancel()
        super.onDestroy()
    }

    private fun initializeOrdersClient() {
        try {
            ordersClient = OrdersClient(
                abi = AndroidLiftNativeAbi(),
                callbackScope = activityScope,
                confirmation = { request ->
                    if (BuildConfig.NETLIFT_STRESS_DIAGNOSTICS) {
                        when (AndroidStressHarness.confirmationMode()) {
                            AndroidStressHarness.ConfirmationMode.ACCEPT -> true
                            AndroidStressHarness.ConfirmationMode.DECLINE -> false
                            AndroidStressHarness.ConfirmationMode.INTERACTIVE,
                            AndroidStressHarness.ConfirmationMode.WAIT_FOR_PROMPT -> requestConfirmation(request)
                        }
                    } else {
                        requestConfirmation(request)
                    }
                },
                onError = { exception ->
                    android.util.Log.e("NetLiftCoreCLR", "Generated Orders ABI callback error", exception)
                }
            )
            status = "CoreCLR ready"
            if (BuildConfig.NETLIFT_STRESS_DIAGNOSTICS) {
                AndroidStressHarness.onActivityReady(this)
                startStressIfRequested()
            }
        } catch (exception: Exception) {
            android.util.Log.e("NetLiftCoreCLR", "Generated Orders ABI initialization failed", exception)
            status = "Generated Orders ABI failed; see logcat"
        }
    }

    private fun placeManagedOrder() {
        val client = ordersClient
        if (client == null) {
            status = "Generated Orders ABI is not ready"
            return
        }
        status = "Calling generated Orders client..."
        activityScope.launch {
            try {
                val receipt = client.placeAsync(OrderDraft("NETLIFT-ANDROID-東京-🙂", 2))
                if (!isDestroyed) {
                    status = "Managed call returned order ${receipt.id}"
                    callbackStatus = "Kotlin received ${receipt.sku} x ${receipt.quantity}"
                }
            } catch (exception: CancellationException) {
                if (!isDestroyed) {
                    status = "Managed order cancelled"
                }
            } catch (exception: LiftAbiException) {
                if (!isDestroyed) {
                    status = if (exception.statusCode == 8 &&
                        exception.message == "Order declined in Kotlin."
                    ) {
                        "Order declined in Kotlin"
                    } else {
                        "Generated Orders call failed (${exception.statusCode}); see logcat"
                    }
                }
            } catch (exception: Exception) {
                android.util.Log.e("NetLiftCoreCLR", "Generated Orders call failed", exception)
                if (!isDestroyed) {
                    status = "Generated Orders call failed; see logcat"
                }
            }
        }
    }

    private suspend fun requestConfirmation(request: NativeConfirmationRequest): Boolean =
        suspendCancellableCoroutine { continuation ->
            if (isDestroyed) {
                continuation.resume(false)
                return@suspendCancellableCoroutine
            }
            confirmationPrompt = request.prompt
            confirmationContinuation = continuation
            if (BuildConfig.NETLIFT_STRESS_DIAGNOSTICS) {
                AndroidStressHarness.onPromptVisible()
            }
            continuation.invokeOnCancellation {
                runOnUiThread {
                    if (confirmationContinuation === continuation) {
                        confirmationContinuation = null
                        confirmationPrompt = null
                    }
                }
            }
        }

    private fun startStressIfRequested() {
        if (intent?.action != STRESS_ACTION || !stressRunStarted.compareAndSet(false, true)) {
            return
        }
        val cycles = intent.getIntExtra(STRESS_CYCLES_EXTRA, 0)
        if (cycles !in 1..50) {
            android.util.Log.e("NetLiftStress", "Rejected invalid stress cycle count: $cycles")
            status = "Stress rejected: cycles must be 1-50"
            stressRunStarted.set(false)
            return
        }
        processScope.launch {
            try {
                runStressCycles(cycles)
            } catch (exception: Exception) {
                AndroidStressHarness.setInteractive()
                android.util.Log.e("NetLiftStress", "STRESS_FAILED cycles=$cycles", exception)
                status = "Stress failed; see logcat"
            }
        }
    }

    private suspend fun runStressCycles(cycles: Int) {
        var activity = this
        var cancelled = 0
        var racedDeclines = 0
        var accepted = 0
        var declined = 0
        android.util.Log.i("NetLiftStress", "STRESS_STARTED cycles=$cycles")

        for (cycle in 1..cycles) {
            val activityForCycle = activity
            val retiringClient = activityForCycle.ordersClient
                ?: throw IllegalStateException("Orders client missing before stress recreation.")
            AndroidStressHarness.waitForPrompt()
            val pendingOrder = processScope.async { activityForCycle.placeStressOrder() }
            withTimeout(STRESS_TIMEOUT_MILLIS) {
                AndroidStressHarness.awaitPrompt()
            }

            AndroidStressHarness.expectActivityRecreation()
            withContext(Dispatchers.Main.immediate) {
                activityForCycle.recreate()
            }
            activity = withTimeout(STRESS_TIMEOUT_MILLIS) {
                AndroidStressHarness.awaitActivity()
            }

            val cancellationResult = try {
                withTimeout(STRESS_TIMEOUT_MILLIS) { pendingOrder.await() }
                throw IllegalStateException("Pending managed order unexpectedly completed during recreation.")
            } catch (exception: TimeoutCancellationException) {
                throw exception
            } catch (exception: CancellationException) {
                "cancelled"
            } catch (exception: LiftAbiException) {
                when (exception.statusCode) {
                    8 -> "raced-decline"
                    11, 12 -> "cancelled-${exception.statusCode}"
                    else -> throw exception
                }
            }
            withTimeout(STRESS_TIMEOUT_MILLIS) { retiringClient.close() }
            if (cancellationResult == "raced-decline") racedDeclines++ else cancelled++

            val shouldAccept = cycle % 2 == 0
            AndroidStressHarness.setDecision(shouldAccept)
            if (shouldAccept) {
                val receipt = withTimeout(STRESS_TIMEOUT_MILLIS) { activity.placeStressOrder() }
                if (receipt.sku != STRESS_SKU || receipt.quantity != STRESS_QUANTITY) {
                    throw IllegalStateException("Stress order returned an unexpected receipt.")
                }
                accepted++
            } else {
                try {
                    withTimeout(STRESS_TIMEOUT_MILLIS) { activity.placeStressOrder() }
                    throw IllegalStateException("Declined stress order unexpectedly succeeded.")
                } catch (exception: LiftAbiException) {
                    if (exception.statusCode != 8 || exception.message != "Order declined in Kotlin.") {
                        throw exception
                    }
                    declined++
                }
            }
            AndroidStressHarness.setInteractive()
            android.util.Log.i(
                "NetLiftStress",
                "STRESS_CYCLE cycle=$cycle pending=$cancellationResult accepted=$shouldAccept"
            )
        }

        status = "Stress complete: $cycles cycles"
        android.util.Log.i(
            "NetLiftStress",
            "STRESS_COMPLETE cycles=$cycles cancelled=$cancelled raced_declines=$racedDeclines accepted=$accepted declined=$declined"
        )
    }

    private suspend fun placeStressOrder(): OrderReceipt =
        ordersClient?.placeAsync(OrderDraft(STRESS_SKU, STRESS_QUANTITY))
            ?: throw IllegalStateException("Orders client is not ready during stress run.")

    private fun resolveConfirmation(accepted: Boolean) {
        val continuation = confirmationContinuation ?: return
        confirmationContinuation = null
        confirmationPrompt = null
        if (continuation.isActive) {
            continuation.resume(accepted)
        }
    }

    private fun installManagedAssets() {
        val target = File(filesDir, "dotnet").apply { mkdirs() }
        val names = assets.list("dotnet")
            ?: throw IOException("Unable to list packaged assets/dotnet")
        if (names.isEmpty()) {
            throw IOException("No managed assets found in packaged assets/dotnet")
        }
        for (name in names) {
            assets.open("dotnet/$name").use { input ->
                File(target, name).outputStream().use(input::copyTo)
            }
        }
    }

    private fun strictUtf8Bytes(value: String): ByteArray {
        val encoder = StandardCharsets.UTF_8.newEncoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
        val encoded = encoder.encode(CharBuffer.wrap(value))
        return ByteArray(encoded.remaining()).also(encoded::get)
    }

    companion object {
        private const val STRESS_ACTION = "com.netlift.androidhost.action.RUN_STRESS"
        private const val STRESS_CYCLES_EXTRA = "netlift.stress.cycles"
        private const val STRESS_TIMEOUT_MILLIS = 15_000L
        private const val STRESS_SKU = "NETLIFT-ANDROID-東京-🙂"
        private const val STRESS_QUANTITY = 2
        private val stressRunStarted = AtomicBoolean()
        private var managedAssetsInstalled = false
        private val processScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        private val managedExecutor = Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "NetLiftManagedHost").apply { isDaemon = true }
        }

        init {
            System.loadLibrary("netlift_host")
        }
    }
}
