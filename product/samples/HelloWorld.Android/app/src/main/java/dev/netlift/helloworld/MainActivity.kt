package dev.netlift.helloworld

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.widget.TextView
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class MainActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val greeting = TextView(this).apply {
            text = "Starting NetLift..."
            textSize = 24f
        }
        setContentView(greeting)

        scope.launch {
            try {
                val client = HelloServiceClient()
                greeting.text = client.greet(
                    GreetingRequest(name = "World")
                ).message
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (error: Exception) {
                Log.e("NetLiftHelloWorld", "Could not invoke the .NET greeting service.", error)
                greeting.text = "Hello World failed: ${error.message}"
            }
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
