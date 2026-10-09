package dev.netlift.workloadlibrarymode.app

import dev.netlift.orders.OrderConfirmationRequest
import dev.netlift.orders.OrderConfirmationResult
import dev.netlift.orders.OrderConfirmationService
import dev.netlift.orders.registerOrderConfirmationService
import kotlinx.coroutines.delay

object CallbackAcceptance {
    @JvmStatic
    fun register(): AutoCloseable =
        registerOrderConfirmationService(
            object : OrderConfirmationService {
                override suspend fun confirmOrder(
                    request: OrderConfirmationRequest,
                ): OrderConfirmationResult {
                    return when (request.orderId) {
                        "order-customer-confirm" ->
                            OrderConfirmationResult(confirmed = true)
                        "order-customer-decline" ->
                            OrderConfirmationResult(confirmed = false)
                        "order-customer-native-failure" ->
                            error("Requested native confirmation failure.")
                        "order-customer-native-cancel" -> {
                            delay(30_000)
                            OrderConfirmationResult(confirmed = true)
                        }
                        "order-customer-revoke" -> {
                            delay(300)
                            OrderConfirmationResult(confirmed = true)
                        }
                        else -> error(
                            "Managed callback supplied an unexpected order ID: ${request.orderId}"
                        )
                    }
                }
            }
        )
}
