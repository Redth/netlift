package com.netlift.sample

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.netlift.androidhost.contract.OrderDraft
import com.netlift.androidhost.contract.OrdersClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

@Composable
internal fun OrdersMinimalScreen(client: OrdersClient) {
    var sku by remember { mutableStateOf("ABC-123") }
    var quantity by remember { mutableStateOf("1") }
    var status by remember { mutableStateOf("Ready to place an order.") }
    var orderJob by remember { mutableStateOf<Job?>(null) }
    val scope = rememberCoroutineScope()
    val count = quantity.toIntOrNull()

    Column(
        modifier = Modifier.padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        OutlinedTextField(sku, onValueChange = { sku = it }, label = { Text("SKU") })
        OutlinedTextField(
            quantity,
            onValueChange = { quantity = it },
            label = { Text("Quantity") }
        )
        Button(
            enabled = orderJob == null && sku.isNotBlank() && count != null && count > 0,
            onClick = {
                val parsed = count ?: return@Button
                orderJob = scope.launch {
                    try {
                        val receipt = client.placeAsync(OrderDraft(sku, parsed))
                        status = "Order #${receipt.id}: ${receipt.sku} x ${receipt.quantity}"
                    } catch (cancelled: CancellationException) {
                        status = "Order cancelled."
                        throw cancelled
                    } catch (failure: Exception) {
                        status = "Order failed: ${failure.message ?: failure.javaClass.simpleName}"
                    } finally {
                        orderJob = null
                    }
                }
            }
        ) {
            Text("Place order")
        }
        if (orderJob != null) {
            Button(onClick = { orderJob?.cancel() }) { Text("Cancel order") }
        }
        Text(status)
    }
}
