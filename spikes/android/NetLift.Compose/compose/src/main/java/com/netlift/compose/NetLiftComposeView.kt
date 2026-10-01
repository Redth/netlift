package com.netlift.compose

import android.content.Context
import android.view.ViewGroup
import android.widget.FrameLayout
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.platform.ComposeView

interface ConfirmationResultCallback {
    fun onResult(confirmed: Boolean)
}

class NetLiftComposeView(context: Context) : FrameLayout(context) {
    private val status = mutableStateOf("CoreCLR host ready")
    private val confirmationTitle = mutableStateOf<String?>(null)
    private var placeOrderAction: Runnable? = null
    private var confirmationResult: ConfirmationResultCallback? = null

    init {
        addView(
            ComposeView(context).apply { setContent { NetLiftComposeContent() } },
            LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
        )
    }

    fun setPlaceOrderAction(action: Runnable) {
        placeOrderAction = action
    }

    fun requestConfirmation(title: String, result: ConfirmationResultCallback) {
        confirmationResult?.onResult(false)
        confirmationResult = result
        confirmationTitle.value = title
    }

    fun showManagedStatus(value: String) {
        status.value = value
    }

    private fun resolveConfirmation(confirmed: Boolean) {
        val result = confirmationResult
        confirmationResult = null
        confirmationTitle.value = null
        result?.onResult(confirmed)
    }

    @Composable
    private fun NetLiftComposeContent() {
        MaterialTheme {
            Column(
                modifier = Modifier.fillMaxSize().padding(24.dp),
                verticalArrangement = Arrangement.Center
            ) {
                Text("NetLift Compose + .NET 11 CoreCLR", style = MaterialTheme.typography.headlineSmall)
                Spacer(Modifier.height(16.dp))
                Text(status.value)
                Spacer(Modifier.height(24.dp))
                Button(onClick = { placeOrderAction?.run() }) {
                    Text("Place sample order")
                }
                confirmationTitle.value?.let { title ->
                    AlertDialog(
                        onDismissRequest = { resolveConfirmation(false) },
                        title = { Text("Confirm order") },
                        text = { Text(title) },
                        confirmButton = {
                            TextButton(onClick = { resolveConfirmation(true) }) {
                                Text("Confirm")
                            }
                        },
                        dismissButton = {
                            TextButton(onClick = { resolveConfirmation(false) }) {
                                Text("Cancel")
                            }
                        }
                    )
                }
            }
        }
    }
}
