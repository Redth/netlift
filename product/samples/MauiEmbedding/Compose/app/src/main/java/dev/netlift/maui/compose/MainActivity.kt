package dev.netlift.maui.compose

import android.os.Bundle
import android.util.Log
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.lifecycleScope
import dev.netlift.embedding.EmbeddingRequest
import dev.netlift.embedding.EmbeddingServiceClient
import dev.netlift.runtime.ManagedNetLiftBridge
import dev.netlift.runtime.NetLiftRuntime
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay

class MainActivity : ComponentActivity() {
    private val bridge by lazy { ManagedNetLiftBridge() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        lifecycleScope.launch {
            NetLiftRuntime.initialize(applicationContext)
            val greeting = EmbeddingServiceClient().greet(EmbeddingRequest("Compose"))
            check(greeting.platform == "net11.0-android")
            acceptance()
            setContent {
                var visible by androidx.compose.runtime.remember { mutableStateOf(true) }
                LaunchedEffect(Unit) {
                    waitForViews(1)
                    repeat(3) {
                        visible = false
                        waitForViews(0)
                        visible = true
                        waitForViews(1)
                    }
                    Log.i("NetLiftAcceptance", "NETLIFT_MAUI_COMPOSE_LIFECYCLE PASS")
                }
                Column(modifier = Modifier.fillMaxSize().systemBarsPadding().padding(24.dp)) {
                    Text(greeting.message)
                    if (visible) {
                        AndroidView(
                            factory = { context ->
                                FrameLayout(context).apply {
                                    addView(bridge.createPlatformView(this))
                                }
                            },
                            modifier = Modifier.height(120.dp),
                            onRelease = { host ->
                                val view = host.getChildAt(0)
                                host.removeView(view)
                                bridge.releasePlatformView(view)
                            },
                        )
                    }
                    Button(onClick = { visible = !visible }) {
                        Text(if (visible) "Release MAUI view" else "Create MAUI view")
                    }
                }
            }
        }
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
