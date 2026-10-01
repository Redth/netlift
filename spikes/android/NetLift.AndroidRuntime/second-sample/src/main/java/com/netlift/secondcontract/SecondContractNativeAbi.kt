package com.netlift.secondcontract

import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.NetLiftNativeRegistrationV2
import com.netlift.androidruntime.NetLiftTypedReply
import com.netlift.androidruntime.NetLiftHost

internal interface SecondContractNativeAbi {
    fun registerCallbacksV2(
        serviceId: Int,
        callbacks: NetLiftNativeCallbacksV2
    ): NetLiftNativeRegistrationV2

    fun revokeV2(registrationToken: Long)
    fun ackDrain(registrationToken: Long, generation: Long)
    fun begin(serviceId: Int, methodId: Int, registrationToken: Long, arguments: ByteArray): Long
    fun activate(operationToken: Long)
    fun cancel(operationToken: Long)
    fun replyTyped(registrationToken: Long, requestToken: Long, reply: NetLiftTypedReply)
}

internal object NetLiftSecondContractNativeAbi : SecondContractNativeAbi {
    override fun registerCallbacksV2(
        serviceId: Int,
        callbacks: NetLiftNativeCallbacksV2
    ): NetLiftNativeRegistrationV2 = NetLiftHost.registerCallbacksV2(serviceId, callbacks)

    override fun revokeV2(registrationToken: Long) =
        NetLiftHost.revokeV2(registrationToken)

    override fun ackDrain(registrationToken: Long, generation: Long) =
        NetLiftHost.ackDrain(registrationToken, generation)

    override fun begin(
        serviceId: Int,
        methodId: Int,
        registrationToken: Long,
        arguments: ByteArray
    ): Long = NetLiftHost.begin(serviceId, methodId, registrationToken, arguments)

    override fun activate(operationToken: Long) =
        NetLiftHost.activate(operationToken)

    override fun cancel(operationToken: Long) =
        NetLiftHost.cancel(operationToken)

    override fun replyTyped(
        registrationToken: Long,
        requestToken: Long,
        reply: NetLiftTypedReply
    ) = NetLiftHost.replyTyped(registrationToken, requestToken, reply)
}
