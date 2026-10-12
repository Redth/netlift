package dev.netlift.maui.compose

import android.os.Bundle
import android.util.Log
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.getValue
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.lifecycle.lifecycleScope
import dev.netlift.embedding.EmbeddingRequest
import dev.netlift.embedding.EmbeddingServiceClient
import dev.netlift.embedding.ClipboardRequest
import dev.netlift.embedding.StorageEntry
import dev.netlift.embedding.StorageKey
import dev.netlift.runtime.ManagedNetLiftBridge
import dev.netlift.runtime.NetLiftRuntime
import dev.netlift.runtime.NetLiftRemoteException
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay

class MainActivity : ComponentActivity() {
    private val bridge by lazy { ManagedNetLiftBridge() }
    private val client by lazy { EmbeddingServiceClient() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.getInsetsController(window, window.decorView).apply {
            isAppearanceLightStatusBars = true
            isAppearanceLightNavigationBars = true
        }
        lifecycleScope.launch {
            NetLiftRuntime.initialize(applicationContext)
            val greeting = client.greet(EmbeddingRequest("Compose"))
            check(greeting.platform == "net11.0-android")
            acceptance()
            setContent {
                var visible by remember { mutableStateOf(true) }
                var deviceInfo by remember { mutableStateOf("Loading MAUI device info...") }
                var secureValue by remember { mutableStateOf("Hello from .NET MAUI") }
                var clipboardText by remember { mutableStateOf("Hello from .NET MAUI") }
                var status by remember { mutableStateOf("Services run in the .NET library") }
                var errorMessage by remember { mutableStateOf<String?>(null) }
                var busy by remember { mutableStateOf(false) }
                val scope = rememberCoroutineScope()
                fun perform(operation: suspend () -> String) {
                    busy = true
                    errorMessage = null
                    scope.launch {
                        try {
                            status = operation()
                        } catch (error: kotlinx.coroutines.CancellationException) {
                            throw error
                        } catch (error: Exception) {
                            errorMessage = error.message ?: error.toString()
                            Log.e("NetLiftSample", "MAUI service operation failed", error)
                        } finally {
                            busy = false
                        }
                    }
                }
                LaunchedEffect(Unit) {
                    waitForViews(1)
                    repeat(3) {
                        visible = false
                        waitForViews(0)
                        visible = true
                        waitForViews(1)
                    }
                    Log.i("NetLiftAcceptance", "NETLIFT_MAUI_COMPOSE_LIFECYCLE PASS")
                    try {
                        val device = client.greet(EmbeddingRequest("Compose"))
                        deviceInfo = "${device.devicePlatform} ${device.deviceVersion} / ${device.deviceModel}"
                    } catch (error: kotlinx.coroutines.CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        errorMessage = error.message ?: error.toString()
                        Log.e("NetLiftSample", "MAUI device info failed", error)
                    }
                }
                BoxWithConstraints(
                    modifier = Modifier.fillMaxSize().background(Color.White)
                        .systemBarsPadding().imePadding(),
                ) {
                    Column(
                        modifier = Modifier.verticalScroll(rememberScrollState())
                            .heightIn(min = maxHeight).padding(16.dp).fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(24.dp, Alignment.CenterVertically),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Text(
                            "Native Compose + .NET MAUI",
                            fontSize = 17.sp,
                            color = Color.Black,
                            textAlign = TextAlign.Center,
                        )
                        if (visible) {
                            AndroidView(
                                factory = { context ->
                                    FrameLayout(context).apply {
                                        addView(
                                            bridge.createPlatformView(this),
                                            FrameLayout.LayoutParams(
                                                ViewGroup.LayoutParams.MATCH_PARENT,
                                                ViewGroup.LayoutParams.MATCH_PARENT,
                                            ),
                                        )
                                    }
                                },
                                modifier = Modifier.fillMaxWidth().height(120.dp),
                                onRelease = { host ->
                                    val view = host.getChildAt(0)
                                    host.removeView(view)
                                    bridge.releasePlatformView(view)
                                },
                            )
                        }
                        ServiceAction(if (visible) "Release MAUI view" else "Create MAUI view") {
                            visible = !visible
                        }
                        Column(
                            modifier = Modifier.fillMaxWidth(),
                            verticalArrangement = Arrangement.spacedBy(12.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                        ) {
                            Text("MAUI Essentials", fontSize = 20.sp, fontWeight = FontWeight.SemiBold, color = Color.Black)
                            Text(deviceInfo, fontSize = 13.sp, color = Color.Black, textAlign = TextAlign.Center)
                            ServiceField(secureValue, "Secure storage value", busy, secret = true) { secureValue = it }
                            Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                ServiceAction("Save secure value", !busy) {
                                    perform {
                                        client.setSecureValue(StorageEntry(key = "netlift.maui.sample", value = secureValue))
                                        "Secure value saved"
                                    }
                                }
                                ServiceAction("Load secure value", !busy) {
                                    perform {
                                        val result = client.getSecureValue(StorageKey("netlift.maui.sample"))
                                        secureValue = result.value ?: ""
                                        if (result.value == null) "No saved secure value" else "Secure value loaded"
                                    }
                                }
                            }
                            ServiceField(clipboardText, "Clipboard text", busy) { clipboardText = it }
                            Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                ServiceAction("Copy clipboard", !busy) {
                                    perform {
                                        client.clipboard(ClipboardRequest(clipboardText))
                                        "Clipboard text copied"
                                    }
                                }
                                ServiceAction("Read clipboard", !busy) {
                                    perform {
                                        val result = client.clipboard(ClipboardRequest(null))
                                        clipboardText = result.value ?: ""
                                        if (result.value == null) "Clipboard is empty" else "Clipboard text read"
                                    }
                                }
                            }
                            Text(
                                errorMessage ?: status,
                                fontSize = 13.sp,
                                color = if (errorMessage == null) Color.Gray else Color.Red,
                                textAlign = TextAlign.Center,
                            )
                        }
                    }
                }
            }
            if (intent.getBooleanExtra("netlift.essentials.acceptance", false)) {
                waitForViews(1)
                essentialsAcceptance()
            }
        }
    }

    private suspend fun essentialsAcceptance() {
        val device = client.greet(EmbeddingRequest("Compose"))
        check(device.devicePlatform == "Android" && device.deviceModel.isNotEmpty() && device.deviceVersion.isNotEmpty())
        val key = StorageKey("netlift.maui.acceptance")
        client.removeSecureValue(key)
        check(client.getSecureValue(key).value == null)
        try {
            for (invalid in listOf(
                StorageEntry(key = key.key, value = ""),
                StorageEntry(key = " ", value = "Demo"),
            )) {
                try {
                    client.setSecureValue(invalid)
                    error("Invalid secure storage input was accepted")
                } catch (error: NetLiftRemoteException) {
                    check(error.status != 0 && error.remoteMessage.isNotEmpty())
                }
            }
            client.setSecureValue(StorageEntry(key = key.key, value = "MAUI storage demo"))
            check(client.getSecureValue(key).value == "MAUI storage demo")
            client.setSecureValue(StorageEntry(key = key.key, value = "Updated MAUI demo"))
            check(client.getSecureValue(key).value == "Updated MAUI demo")
        } finally {
            client.removeSecureValue(key)
        }
        check(client.getSecureValue(key).value == null)
        check(client.clipboard(ClipboardRequest("")).value.isNullOrEmpty())
        client.clipboard(ClipboardRequest("NetLift MAUI clipboard demo"))
        check(client.clipboard(ClipboardRequest(null)).value == "NetLift MAUI clipboard demo")
        Log.i("NetLiftAcceptance", "NETLIFT_MAUI_ESSENTIALS android device-info secure-storage clipboard PASS")
    }

    private fun acceptance() {
        val compose = ComposeView(this)
        check(bridge.roundTripBorrowedView(compose) === compose)
        check(compose.contentDescription == "NETLIFT_MANAGED_ROUNDTRIP")
        val host = FrameLayout(this)
        val baseline = bridge.livePlatformViewCount()
        repeat(3) {
            val view = bridge.createPlatformView(host)
            val text = checkNotNull(textView(view))
            check(text.text.toString() == "MAUI label created in C#")
            host.addView(view, ViewGroup.LayoutParams(600, 200))
            bridge.collectManagedGarbage()
            System.gc()
            check(bridge.roundTripBorrowedView(view) === view)
            check(text.text.toString() == "MAUI label updated in C#")
            host.removeView(view)
            bridge.releasePlatformView(view)
            check(bridge.livePlatformViewCount() == baseline)
        }

        Log.i("NetLiftAcceptance", "NETLIFT_MAUI_ACCEPTANCE android tfm compose-identity managed-update gc release-recreate PASS")
    }

    private fun textView(view: android.view.View): TextView? {
        if (view is TextView) return view
        if (view is ViewGroup) {
            for (index in 0 until view.childCount) {
                textView(view.getChildAt(index))?.let { return it }
            }
        }
        return null
    }

    private suspend fun waitForViews(count: Int) {
        repeat(200) {
            if (bridge.livePlatformViewCount() == count) return
            delay(10)
        }
        error("Compose did not reach $count factory-owned views")
    }
}

@Composable
private fun ServiceAction(label: String, enabled: Boolean = true, onClick: () -> Unit) {
    TextButton(
        onClick = onClick,
        enabled = enabled,
        colors = ButtonDefaults.textButtonColors(contentColor = Color(0xFF007AFF)),
    ) {
        Text(label, fontSize = 17.sp, fontWeight = FontWeight.Normal)
    }
}

@Composable
private fun ServiceField(
    value: String,
    placeholder: String,
    busy: Boolean,
    secret: Boolean = false,
    onValueChange: (String) -> Unit,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        placeholder = { Text(placeholder) },
        enabled = !busy,
        singleLine = true,
        modifier = Modifier.fillMaxWidth().height(56.dp)
            .semantics { contentDescription = placeholder },
        textStyle = TextStyle(fontSize = 17.sp),
        visualTransformation = if (secret) PasswordVisualTransformation() else VisualTransformation.None,
        colors = OutlinedTextFieldDefaults.colors(
            focusedTextColor = Color.Black,
            unfocusedTextColor = Color.Black,
            focusedBorderColor = Color(0xFF007AFF),
            unfocusedBorderColor = Color.Gray,
            cursorColor = Color(0xFF007AFF),
        ),
    )
}
