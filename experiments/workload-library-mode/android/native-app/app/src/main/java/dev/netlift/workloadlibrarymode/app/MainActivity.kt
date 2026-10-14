package dev.netlift.workloadlibrarymode.app

import android.app.Activity
import android.os.Bundle
import android.widget.TextView
import dev.netlift.orders.NetLiftAndroidTransport
import dev.netlift.orders.OrderConfirmation
import dev.netlift.orders.OrderConfirmationRequest
import dev.netlift.orders.OrderConfirmationResult
import dev.netlift.orders.OrderPriority
import dev.netlift.orders.OrderRequest
import dev.netlift.orders.OrdersServiceClient
import dev.netlift.runtime.NetLiftRuntime
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch

class MainActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

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

            val transport = NetLiftAndroidTransport
            val orders = OrdersServiceClient(transport)
            scope.launch {
                val result = orders.submitOrder(
                    OrderRequest(
                        customerId = "customer-42",
                        quantity = 2,
                        priority = OrderPriority.Expedited,
                        note = "Generated Kotlin client \uD83D\uDE80",
                    )
                )
                check(result.accepted && result.orderId == "order-customer-42") {
                    "Generated Orders client returned an unexpected result: $result"
                }
                val confirmation = orders.confirmOrder(
                    OrderRequest(
                        customerId = "callback",
                        quantity = 3,
                        priority = OrderPriority.Standard,
                        note = null,
                    ),
                    object : OrderConfirmation {
                        override suspend fun confirm(
                            request: OrderConfirmationRequest,
                        ): OrderConfirmationResult = OrderConfirmationResult(
                            accepted = request.quantity == 3,
                            reason = "confirmed by Kotlin",
                        )
                    },
                )
                check(confirmation.accepted &&
                    confirmation.message == "confirmed by Kotlin") {
                    "Generated native callback returned an unexpected result: $confirmation"
                }
                check(NetLiftRuntime.diagnostics().globalReferenceCount == 2) {
                    "Generated callback registration leaked a JNI global reference."
                }
                val events = orders.watchOrder(
                    OrderRequest(
                        customerId = "customer-42",
                        quantity = 2,
                        priority = OrderPriority.Expedited,
                        note = "Generated Kotlin stream",
                    )
                ).toList()
                check(events.map { it.state } == listOf("accepted", "complete")) {
                    "Generated Orders stream returned unexpected events: $events"
                }
                val firstEvent = orders.watchOrder(
                    OrderRequest(
                        customerId = "customer-43",
                        quantity = 1,
                        priority = OrderPriority.Standard,
                        note = null,
                    )
                ).take(1).toList()
                check(firstEvent.size == 1 && firstEvent.single().state == "accepted") {
                    "Cancelling stream collection did not preserve the first event."
                }
                val invalidRoute = runCatching {
                    transport.invoke(
                        OrdersServiceClient.ServiceId,
                        UInt.MAX_VALUE,
                        "{}".encodeToByteArray(),
                    )
                }.exceptionOrNull()
                check(!invalidRoute?.message.isNullOrBlank()) {
                    "Managed route failures were not translated to Kotlin exceptions."
                }
                val cancellation = scope.launch {
                    try {
                        orders.submitOrder(
                            OrderRequest(
                                customerId = "cancel",
                                quantity = 1,
                                priority = OrderPriority.Standard,
                                note = "NETLIFT-CANCEL",
                            )
                        )
                        error("Cancelled generated call completed successfully.")
                    } catch (_: CancellationException) {
                    }
                }
                delay(100)
                cancellation.cancelAndJoin()

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
                    Native callback: ${confirmation.message}
                    ${result.message}
                    Coroutine cancellation propagated to .NET.
                    Generated stream emitted ${events.size} events and closed early.
                """.trimIndent()
            }
        } catch (error: Throwable) {
            status.text = "Initialization failed: ${error.message}"
            throw error
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
