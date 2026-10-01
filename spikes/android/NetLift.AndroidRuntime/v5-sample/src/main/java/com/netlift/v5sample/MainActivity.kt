package com.netlift.v5sample

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.lifecycleScope
import com.netlift.androidruntime.v5.NetLiftV5Endpoint
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    private var activeEndpoint: NetLiftV5Endpoint? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        var status by mutableStateOf("Ready to start compatible-start v5.")
        var running by mutableStateOf(false)

        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(24.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp)
                    ) {
                        Text("NetLift Android v5 / .NET 11 CoreCLR")
                        Text(status)
                        Button(
                            enabled = !running && activeEndpoint == null,
                            onClick = {
                                running = true
                                status = "Starting CoreCLR and compatible-start…"
                                lifecycleScope.launch {
                                    try {
                                        val proof = withContext(Dispatchers.IO) {
                                            val endpoint = NetLiftV5Endpoint.open(applicationContext)
                                            try {
                                                val peer = endpoint.start(
                                                    V5BuildIdentity.expectedManagedSchemaHashHex
                                                )
                                                val route = endpoint.dispatch(
                                                    peer,
                                                    V5SampleCodec.encode(peer)
                                                )
                                                val counters = endpoint.routeCounters()
                                                Proof(
                                                    endpoint,
                                                    peer.localSchemaHashHex,
                                                    peer.schemaHashHex,
                                                    route,
                                                    counters.routeCalls
                                                )
                                            } catch (failure: Exception) {
                                                endpoint.close()
                                                throw failure
                                            }
                                        }
                                        activeEndpoint = proof.endpoint
                                        status = "CoreCLR route=${
                                            proof.routeValue
                                        }; managed=${proof.managedHash.take(12)} " +
                                            "peer=${proof.peerHash.take(12)}; " +
                                            "managed route calls=${proof.routeCalls}."
                                    } catch (failure: Exception) {
                                        status = "v5 host failed: ${failure.message}"
                                    } finally {
                                        running = false
                                    }
                                }
                            }
                        ) {
                            Text("Start and dispatch")
                        }
                    }
                }
            }
        }
    }

    override fun onDestroy() {
        activeEndpoint?.close()
        activeEndpoint = null
        super.onDestroy()
    }

    private data class Proof(
        val endpoint: NetLiftV5Endpoint,
        val managedHash: String,
        val peerHash: String,
        val routeValue: Int,
        val routeCalls: Long
    )
}
