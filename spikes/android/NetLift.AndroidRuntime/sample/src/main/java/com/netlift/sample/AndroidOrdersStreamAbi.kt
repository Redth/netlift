package com.netlift.sample

import android.content.Context
import com.netlift.androidhost.contract.LiftNativeStreamAbi
import com.netlift.androidhost.contract.LiftStreamCallbacks
import com.netlift.androidhost.contract.LiftStreamRuntimeInfo
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftStreamCallbacks as RuntimeStreamCallbacks

internal class AndroidOrdersStreamAbi(context: Context) : LiftNativeStreamAbi {
    private val applicationContext = context.applicationContext

    override fun start(abiVersion: Int, schemaHash: ByteArray): LiftStreamRuntimeInfo {
        val runtime = NetLiftHost.start(applicationContext, abiVersion, schemaHash)
        return LiftStreamRuntimeInfo(runtime.abiVersion, runtime.featureBits, runtime.schemaHash)
    }

    override fun subscribe(
        serviceId: Int,
        subscriptionId: Int,
        callbacks: LiftStreamCallbacks,
        initialCredit: Int,
        transportCapacity: Int,
        maxItemBytes: Int
    ): Long = NetLiftHost.subscribeStream(
        serviceId,
        subscriptionId,
        byteArrayOf(),
        CallbackForwarder(callbacks),
        initialCredit,
        transportCapacity,
        maxItemBytes
    )

    override fun activate(token: Long) = NetLiftHost.activateStream(token)

    override fun request(token: Long, credits: Int) =
        NetLiftHost.requestStream(token, credits)

    override fun unsubscribe(token: Long) = NetLiftHost.unsubscribeStream(token)

    private class CallbackForwarder(
        private val callbacks: LiftStreamCallbacks
    ) : RuntimeStreamCallbacks {
        override fun onNext(token: Long, borrowedItem: ByteArray) =
            callbacks.onNext(token, borrowedItem.copyOf())

        override fun onTerminal(token: Long, status: Int, diagnostic: ByteArray) =
            callbacks.onTerminal(token, status, diagnostic.copyOf())

        override fun onRelease(token: Long) = callbacks.onRelease(token)
    }
}
