package dev.netlift.workloadlibrarymode.app

import android.app.Activity
import android.os.Bundle
import android.widget.TextView
import dev.netlift.orders.OrderPriority
import dev.netlift.orders.OrderRequest
import dev.netlift.orders.OrdersServiceClient
import dev.netlift.runtime.ManagedNetLiftBridge
import dev.netlift.runtime.NetLiftRuntime
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

class MainActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val status = TextView(this)
        status.textSize = 18f
        status.setPadding(32, 64, 32, 32)
        setContentView(status)

        try {
            NetLiftRuntime.initialize(applicationContext)
            // Verify repeated initialization is safe and does not start CoreCLR twice.
            NetLiftRuntime.initialize(applicationContext)
            check(NetLiftRuntime.isInitialized()) {
                ".NET Android workload component did not report ready."
            }
            check(NetLiftRuntime.diagnostics().globalReferenceCount == 2) {
                "NetLift retained an unexpected number of JNI global references."
            }

            val bridge = ManagedNetLiftBridge()
            val transport = ManagedNetLiftTransport()
            val orders = OrdersServiceClient(transport)
            val result = runImmediate {
                orders.submitOrder(
                    OrderRequest(
                        customerId = "customer-42",
                        quantity = 2,
                        priority = OrderPriority.Expedited,
                        note = "Generated Kotlin client \uD83D\uDE80",
                    )
                )
            }
            check(result.accepted && result.orderId == "order-customer-42") {
                "Generated Orders client returned an unexpected result: $result"
            }
            check(bridge.echoView(status) === status) {
                "Android View peer identity was not preserved."
            }
            val invalidRoute = runCatching {
                runImmediate {
                    transport.invoke(
                        OrdersServiceClient.ServiceId,
                        UInt.MAX_VALUE,
                        "{}".encodeToByteArray(),
                    )
                }
            }.exceptionOrNull()
            check(!invalidRoute?.message.isNullOrBlank()) {
                "Managed route failures were not translated to Kotlin exceptions."
            }
            val nativeThreadFailure = runCatching {
                NetLiftRuntime.invokeFromNativeThreadForDiagnostics(
                    OrdersServiceClient.ServiceId,
                    UInt.MAX_VALUE,
                    "{}".encodeToByteArray(),
                )
            }.exceptionOrNull()
            check(!nativeThreadFailure?.message.isNullOrBlank()) {
                "JNI native-thread attachment did not preserve managed error translation."
            }
            check(NetLiftRuntime.diagnostics().globalReferenceCount == 2) {
                "A native-thread invocation leaked JNI global references."
            }

            status.text = """
                .NET Android workload component initialized explicitly.
                Generated Orders client: ${result.orderId}
                ${result.message}
                Android View peer identity preserved.
            """.trimIndent()
        } catch (error: Throwable) {
            status.text = "Initialization failed: ${error.message}"
            throw error
        }
    }

    private fun <T> runImmediate(block: suspend () -> T): T {
        var outcome: Result<T>? = null
        block.startCoroutine(
            Continuation(EmptyCoroutineContext) { result ->
                outcome = result
            }
        )
        return checkNotNull(outcome) {
            "The proof transport unexpectedly suspended."
        }.getOrThrow()
    }
}
