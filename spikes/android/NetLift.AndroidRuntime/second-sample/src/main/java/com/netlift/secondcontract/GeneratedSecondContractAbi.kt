package com.netlift.secondcontract

import android.content.Context
import com.netlift.androidhost.contract.LiftNativeCallbackLifecycleV2Abi
import com.netlift.androidhost.contract.LiftNativeCallbacks
import com.netlift.androidhost.contract.LiftNativeCallbacksV2
import com.netlift.androidhost.contract.LiftNativeRegistrationV2
import com.netlift.androidhost.contract.LiftNativeReply
import com.netlift.androidhost.contract.LiftNativeRuntimeInfo
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeCallbacks
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.NetLiftTypedReply

internal class GeneratedSecondContractAbi(private val context: Context) :
    LiftNativeCallbackLifecycleV2Abi {
    override fun start(abiVersion: Int, schemaHash: ByteArray): LiftNativeRuntimeInfo {
        val runtime = NetLiftHost.start(context, abiVersion, schemaHash)
        return LiftNativeRuntimeInfo(
            runtime.abiVersion,
            runtime.featureBits,
            runtime.schemaHash.copyOf()
        )
    }

    override fun registerCallbacks(serviceId: Int, callbacks: LiftNativeCallbacks): Long =
        NetLiftHost.registerCallbacks(serviceId, callbackAdapter(callbacks))

    override fun registerCallbacksV2(
        serviceId: Int,
        callbacks: LiftNativeCallbacksV2
    ): LiftNativeRegistrationV2 {
        val adapter = callbackAdapter(callbacks)
        val registration = NetLiftHost.registerCallbacksV2(
            serviceId,
            object : NetLiftNativeCallbacksV2 {
                override fun onRequest(
                    registrationToken: Long,
                    requestToken: Long,
                    methodId: Int,
                    payload: ByteArray
                ) = adapter.onRequest(registrationToken, requestToken, methodId, payload)

                override fun onResult(
                    operationToken: Long,
                    status: Int,
                    result: ByteArray,
                    error: ByteArray
                ) = adapter.onResult(operationToken, status, result, error)

                override fun onRelease(registrationToken: Long) =
                    adapter.onRelease(registrationToken)

                override fun onRevoke(registrationToken: Long, generation: Long) =
                    callbacks.onRevoke(registrationToken, generation)
            }
        )
        return LiftNativeRegistrationV2(registration.token, registration.generation)
    }

    override fun revoke(registrationToken: Long) = NetLiftHost.revoke(registrationToken)

    override fun revokeV2(registrationToken: Long) = NetLiftHost.revokeV2(registrationToken)

    override fun ackDrain(registrationToken: Long, generation: Long) =
        NetLiftHost.ackDrain(registrationToken, generation)

    override fun begin(
        serviceId: Int,
        methodId: Int,
        registrationToken: Long,
        arguments: ByteArray
    ): Long = NetLiftHost.begin(serviceId, methodId, registrationToken, arguments)

    override fun activate(operationToken: Long) = NetLiftHost.activate(operationToken)

    override fun cancel(operationToken: Long) = NetLiftHost.cancel(operationToken)

    override fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean) =
        NetLiftHost.reply(registrationToken, requestToken, accepted)

    override fun replyTyped(registrationToken: Long, requestToken: Long, reply: LiftNativeReply) =
        NetLiftHost.replyTyped(
            registrationToken,
            requestToken,
            NetLiftTypedReply(reply.outcome, reply.accepted, reply.diagnostic.copyOf())
        )

    private fun callbackAdapter(callbacks: LiftNativeCallbacks): NetLiftNativeCallbacks =
        object : NetLiftNativeCallbacks {
            override fun onRequest(
                registrationToken: Long,
                requestToken: Long,
                methodId: Int,
                payload: ByteArray
            ) = callbacks.onRequest(registrationToken, requestToken, methodId, payload.copyOf())

            override fun onResult(
                operationToken: Long,
                status: Int,
                result: ByteArray,
                error: ByteArray
            ) = callbacks.onResult(operationToken, status, result.copyOf(), error.copyOf())

            override fun onRelease(registrationToken: Long) =
                callbacks.onRelease(registrationToken)
        }
}
