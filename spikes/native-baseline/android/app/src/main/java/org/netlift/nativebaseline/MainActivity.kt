package org.netlift.nativebaseline

import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            val application = LocalContext.current.applicationContext as NativeBaselineApplication
            LaunchedEffect(Unit) {
                val elapsedMillis =
                    (android.os.SystemClock.elapsedRealtimeNanos() -
                        application.processStartNanos) / 1_000_000
                Log.i(
                    "NativeBaseline",
                    "NATIVE_BASELINE_UI_READY elapsed_ms=$elapsedMillis"
                )
            }
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                    ) {
                        Text("Native Android baseline", style = MaterialTheme.typography.titleLarge)
                        Text("Jetpack Compose only")
                    }
                }
            }
        }
    }
}
