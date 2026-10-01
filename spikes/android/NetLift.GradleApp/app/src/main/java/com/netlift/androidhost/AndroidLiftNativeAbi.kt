package com.netlift.androidhost

import com.netlift.androidhost.contract.LiftNativeAbi
import com.netlift.androidhost.contract.LiftNativeCallbacks
import com.netlift.androidhost.contract.LiftNativeRuntimeInfo

internal class AndroidLiftNativeAbi : LiftNativeAbi {
    private external fun nativeStart(
        abiVersion: Int,
        schemaHash: ByteArray
    ): LiftNativeRuntimeInfo

    private external fun nativeRegisterCallbacks(
        serviceId: Int,
        callbacks: LiftNativeCallbacks
    ): Long

    private external fun nativeRevoke(registrationToken: Long)

    private external fun nativeBegin(
        serviceId: Int,
        methodId: Int,
        registrationToken: Long,
        arguments: ByteArray
    ): Long

    private external fun nativeActivate(operationToken: Long)

    private external fun nativeCancel(operationToken: Long)

    private external fun nativeReply(
        registrationToken: Long,
        requestToken: Long,
        accepted: Boolean
    )

    override fun start(abiVersion: Int, schemaHash: ByteArray): LiftNativeRuntimeInfo =
        nativeStart(abiVersion, schemaHash)

    override fun registerCallbacks(
        serviceId: Int,
        callbacks: LiftNativeCallbacks
    ): Long = nativeRegisterCallbacks(serviceId, callbacks)

    override fun revoke(registrationToken: Long) = nativeRevoke(registrationToken)

    override fun begin(
        serviceId: Int,
        methodId: Int,
        registrationToken: Long,
        arguments: ByteArray
    ): Long = nativeBegin(serviceId, methodId, registrationToken, arguments)

    override fun activate(operationToken: Long) = nativeActivate(operationToken)

    override fun cancel(operationToken: Long) = nativeCancel(operationToken)

    override fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean) =
        nativeReply(registrationToken, requestToken, accepted)
}
