package dev.netlift.workloadlibrarymode.app

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.widget.TextView
import dev.netlift.orders.AdapterRoundTripRequest
import dev.netlift.orders.CollectionRoundTripRequest
import dev.netlift.orders.OrderPriority
import dev.netlift.orders.OrderRequest
import dev.netlift.orders.OrderStatusRequest
import dev.netlift.orders.OrdersServiceClient
import dev.netlift.orders.UuidRoundTripRequest
import dev.netlift.runtime.NetLiftRemoteException
import dev.netlift.runtime.NetLiftRuntime
import dev.netlift.orders.NetLiftTransport
import dev.netlift.orders.NetLiftAndroidViewClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withContext
import kotlinx.coroutines.flow.toList

class MainActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val status = TextView(this)
        status.textSize = 18f
        status.setPadding(32, 64, 32, 32)
        setContentView(status)

        scope.launch {
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
            val androidViews = NetLiftAndroidViewClient()
            val retainedBaseline = androidViews.liveRetainedViewCount
            val borrowedView = android.view.View(this@MainActivity)
            check(androidViews.roundTripBorrowed(borrowedView) === borrowedView) {
                "Borrowed Android View identity was not preserved."
            }
            check(androidViews.acceptsViewPeer(borrowedView)) {
                "Android View peer was rejected."
            }
            check(!androidViews.acceptsViewPeer(android.graphics.drawable.ColorDrawable())) {
                "Wrong Android peer class was accepted as a View."
            }
            var retainedSource: android.view.View? = android.view.View(this@MainActivity)
            val weakRetainedView = java.lang.ref.WeakReference(retainedSource)
            val retainedIdentity = System.identityHashCode(retainedSource)
            val retainedToken = androidViews.retain(retainedSource!!)
            retainedSource = null
            androidViews.collectManagedGarbage()
            System.gc()
            delay(10)
            val retainedView = checkNotNull(androidViews.retainedView(retainedToken)) {
                "Retained Android View was released early."
            }
            check(System.identityHashCode(retainedView) == retainedIdentity)
            check(weakRetainedView.get() === retainedView)
            check(androidViews.liveRetainedViewCount == retainedBaseline + 1)
            check(androidViews.release(retainedToken) == 0)
            check(androidViews.release(retainedToken) == 6)
            check(androidViews.liveRetainedViewCount == retainedBaseline)
            check(
                withContext(Dispatchers.Default) {
                    runCatching {
                        androidViews.roundTripBorrowed(borrowedView)
                    }.isFailure
                }
            ) {
                "Android View access was not rejected off the main thread."
            }
            check(NetLiftRuntime.diagnostics().globalReferenceCount == 2) {
                "Android View leases did not return JNI global references to baseline."
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_VIEW_PASSED")

            val transport = ManagedNetLiftTransport()
            val orders = OrdersServiceClient(transport, transport)
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
            val legacyRequestResult = org.json.JSONObject(
                transport.invoke(
                    OrdersServiceClient.ServiceId,
                    OrdersServiceClient.SubmitOrderAsyncId,
                    """{"CustomerId":"legacy-customer","Quantity":1,"Priority":"Standard"}"""
                        .encodeToByteArray(),
                ).decodeToString()
            )
            check(
                legacyRequestResult.getBoolean("Accepted") &&
                    legacyRequestResult.getString("OrderId") == "order-legacy-customer"
            ) {
                "The new runtime rejected the legacy request shape."
            }
            val legacyResponse = malformedClient(
                """{"OrderId":"order-legacy-runtime","Accepted":true}"""
            ).submitOrder(
                OrderRequest(
                    customerId = "new-client",
                    quantity = 1,
                    priority = OrderPriority.Standard,
                    note = "optional",
                )
            )
            check(
                legacyResponse.orderId == "order-legacy-runtime" &&
                    legacyResponse.accepted &&
                    legacyResponse.message == null
            ) {
                "The new client rejected the legacy response shape: $legacyResponse"
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_CROSS_VERSION_PASSED")
            val typedError = runCatching {
                orders.submitOrder(
                    OrderRequest(
                        customerId = "customer-42",
                        quantity = 1,
                        priority = OrderPriority.Standard,
                        note = "NETLIFT-ERROR",
                    )
                )
            }.exceptionOrNull()
            check(typedError is NetLiftRemoteException && typedError.status == 11) {
                "Managed handler failure did not preserve its status: $typedError"
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_RESULT_ERROR_PASSED")
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
            val nativeThreadFailure = runCatching {
                withContext(Dispatchers.IO) {
                    NetLiftRuntime.invokeFromNativeThreadForDiagnostics(
                        OrdersServiceClient.ServiceId,
                        UInt.MAX_VALUE,
                        "{}".encodeToByteArray(),
                    )
                }
            }.exceptionOrNull()
            check(!nativeThreadFailure?.message.isNullOrBlank()) {
                "JNI native-thread attachment did not preserve managed error translation."
            }
            check(NetLiftRuntime.diagnostics().globalReferenceCount == 2) {
                "A native-thread invocation leaked JNI global references."
            }

            val cancellable = async {
                orders.submitOrder(
                    OrderRequest(
                        customerId = "customer-cancel",
                        quantity = 1,
                        priority = OrderPriority.Standard,
                        note = "NETLIFT-CANCEL",
                    )
                )
            }
            delay(100)
            cancellable.cancel()
            val cancellation = runCatching {
                cancellable.await()
            }.exceptionOrNull()
            check(cancellation is CancellationException) {
                "Managed cancellation returned an unexpected result: $cancellation"
            }
            check(NetLiftRuntime.diagnostics().globalReferenceCount == 2) {
                "Cancellation leaked JNI global references."
            }

            val uuid = java.util.UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
            val uuidResult = orders.roundTripUuid(UuidRoundTripRequest(uuid))
            check(uuidResult.value == uuid) {
                "Managed UUID adapter returned ${uuidResult.value} instead of $uuid."
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_UUID_PASSED")

            val timestamp = java.time.OffsetDateTime.parse("2026-10-09T12:34:56.789-04:00")
            val adapterPayload = byteArrayOf(0, 1, 127, -128, -1)
            val resource = java.net.URI.create("https://example.test/orders/42?mode=adapter")
            val adapterResult = orders.roundTripAdapters(
                AdapterRoundTripRequest(
                    timestamp = timestamp,
                    payload = adapterPayload,
                    resource = resource,
                )
            )
            check(
                adapterResult.timestamp == timestamp &&
                    adapterResult.payload.contentEquals(adapterPayload) &&
                    adapterResult.resource == resource
            ) {
                "Managed value adapters returned unexpected values: $adapterResult"
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_ADAPTERS_PASSED")
            check(runCatching {
                malformedClient("""{"Value":"not-a-uuid"}""")
                    .roundTripUuid(UuidRoundTripRequest(uuid))
            }.isFailure)
            check(runCatching {
                malformedClient(
                    """{"Payload":"AAE=","Timestamp":"not-a-timestamp","Resource":"https://example.test"}"""
                ).roundTripAdapters(
                    AdapterRoundTripRequest(
                        timestamp = timestamp,
                        payload = adapterPayload,
                        resource = resource,
                    )
                )
            }.isFailure)
            check(runCatching {
                malformedClient(
                    """{"Payload":"%%%","Timestamp":"2026-10-09T16:34:56.789Z","Resource":"https://example.test"}"""
                ).roundTripAdapters(
                    AdapterRoundTripRequest(
                        timestamp = timestamp,
                        payload = adapterPayload,
                        resource = resource,
                    )
                )
            }.isFailure)
            check(runCatching {
                malformedClient(
                    """{"Payload":"AAE=","Timestamp":"2026-10-09T16:34:56.789Z","Resource":"http://["}"""
                ).roundTripAdapters(
                    AdapterRoundTripRequest(
                        timestamp = timestamp,
                        payload = adapterPayload,
                        resource = resource,
                    )
                )
            }.isFailure)
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_MALFORMED_ADAPTERS_PASSED")

            val values = listOf(Int.MIN_VALUE, -1, 0, 1, Int.MAX_VALUE)
            val priorities = listOf(OrderPriority.Standard, OrderPriority.Expedited)
            val collectionResult = orders.roundTripCollections(
                CollectionRoundTripRequest(
                    values = values,
                    priorities = priorities,
                )
            )
            check(
                collectionResult.values == values &&
                    collectionResult.priorities == priorities
            ) {
                "Managed collections returned unexpected values: $collectionResult"
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_COLLECTIONS_PASSED")

            val updates = orders.watchOrderStatus(
                OrderStatusRequest("order-customer-42")
            ).toList()
            check(
                updates.map { it.sequence } == listOf(1, 2, 3) &&
                    updates.last().message == "Completed"
            ) {
                "Managed status stream returned unexpected updates: $updates"
            }
            val cancellableStream = async {
                orders.watchOrderStatus(
                    OrderStatusRequest("order-stream-cancel")
                ).toList()
            }
            delay(100)
            cancellableStream.cancel()
            check(
                runCatching { cancellableStream.await() }.exceptionOrNull() is CancellationException
            ) {
                "Managed status stream cancellation was not propagated."
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_STREAM_PASSED")

            val callbackType = try {
                Class.forName(
                    "dev.netlift.workloadlibrarymode.app.CallbackAcceptance"
                )
            } catch (_: ClassNotFoundException) {
                null
            }
            val callbackRegistration = callbackType
                ?.getMethod("register")
                ?.invoke(null) as AutoCloseable?
            var callbackPassed = false
            if (callbackRegistration != null) {
                check(NetLiftRuntime.diagnostics().globalReferenceCount == 3) {
                    "Native-service registration did not retain exactly one JNI reference."
                }
                val confirmed = orders.submitOrder(
                    OrderRequest(
                        customerId = "customer-confirm",
                        quantity = 3,
                        priority = OrderPriority.Expedited,
                        note = "NETLIFT-CONFIRM",
                    )
                )
                check(confirmed.accepted && confirmed.orderId == "order-customer-confirm") {
                    "Native order confirmation returned an unexpected result: $confirmed"
                }
                val declined = orders.submitOrder(
                    OrderRequest(
                        customerId = "customer-decline",
                        quantity = 3,
                        priority = OrderPriority.Standard,
                        note = "NETLIFT-CONFIRM",
                    )
                )
                check(!declined.accepted) {
                    "Native order confirmation decline was ignored: $declined"
                }
                val nativeFailure = runCatching {
                    orders.submitOrder(
                        OrderRequest(
                            customerId = "customer-native-failure",
                            quantity = 1,
                            priority = OrderPriority.Standard,
                            note = "NETLIFT-CONFIRM",
                        )
                    )
                }.exceptionOrNull()
                check(!nativeFailure?.message.isNullOrBlank()) {
                    "Native confirmation failure was not translated."
                }

                val nativeCancellation = async {
                    orders.submitOrder(
                        OrderRequest(
                            customerId = "customer-native-cancel",
                            quantity = 1,
                            priority = OrderPriority.Standard,
                            note = "NETLIFT-CONFIRM",
                        )
                    )
                }
                delay(100)
                nativeCancellation.cancel()
                check(runCatching { nativeCancellation.await() }.exceptionOrNull() is CancellationException) {
                    "Native confirmation cancellation was not propagated."
                }

                val revokedFailure = supervisorScope {
                    val revokedInvocation = async {
                        orders.submitOrder(
                            OrderRequest(
                                customerId = "customer-revoke",
                                quantity = 1,
                                priority = OrderPriority.Standard,
                                note = "NETLIFT-CONFIRM",
                            )
                        )
                    }
                    delay(100)
                    callbackRegistration.close()
                    runCatching { revokedInvocation.await() }.exceptionOrNull()
                }
                check(!revokedFailure?.message.isNullOrBlank()) {
                    "Revoked native confirmation accepted a stale reply."
                }
                check(NetLiftRuntime.diagnostics().globalReferenceCount == 2) {
                    "Native-service revoke did not release its JNI reference."
                }
                callbackPassed = true
                Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_CALLBACK_PASSED")
            }
            Log.i("NetLiftAcceptance", "NETLIFT_ANDROID_CANCELLATION_PASSED")
            status.text = """
                .NET Android workload component initialized explicitly.
                Generated Orders client: ${result.orderId}
                ${result.message}
                Cancellation: passed
                Callback: ${if (callbackPassed) "passed" else "not enabled"}
                Stream: passed
            """.trimIndent()
            } catch (error: Throwable) {
                status.text = "Initialization failed: ${error.message}"
                Log.e("NetLiftAcceptance", "NETLIFT_ANDROID_ACCEPTANCE_FAILED", error)
                throw error
            }
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun malformedClient(response: String): OrdersServiceClient =
        OrdersServiceClient(object : NetLiftTransport {
            override suspend fun invoke(
                serviceId: UInt,
                memberId: UInt,
                payload: ByteArray,
            ): ByteArray = response.encodeToByteArray()
        })
}
