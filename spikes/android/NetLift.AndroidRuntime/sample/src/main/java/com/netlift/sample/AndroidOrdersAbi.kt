package com.netlift.sample

import android.content.Context
import com.netlift.androidhost.contract.LiftNativeCallbacks as OrdersCallbacks
import com.netlift.androidhost.contract.LiftNativeReply as OrdersReply
import com.netlift.androidhost.contract.LiftNativeRuntimeInfo as OrdersRuntimeInfo
import com.netlift.androidhost.contract.LiftNativeRegistrationV2 as OrdersRegistrationV2
import com.netlift.androidhost.contract.LiftNativeCallbacksV2 as OrdersCallbacksV2
import com.netlift.androidhost.contract.LiftNativeCallbackLifecycleV2Abi
import com.netlift.androidruntime.NetLiftHost
import com.netlift.androidruntime.NetLiftNativeCallbacks
import com.netlift.androidruntime.NetLiftNativeCallbacksV2
import com.netlift.androidruntime.NetLiftTypedReply
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

internal class AndroidOrdersAbi(context: Context) : LiftNativeCallbackLifecycleV2Abi {
    private val applicationContext = context.applicationContext
    private val v2Registrations = ConcurrentHashMap.newKeySet<Long>()

    override fun start(abiVersion: Int, schemaHash: ByteArray): OrdersRuntimeInfo {
        val runtime = NetLiftHost.start(applicationContext, abiVersion, schemaHash)
        return OrdersRuntimeInfo(runtime.abiVersion, runtime.featureBits, runtime.schemaHash)
    }

    override fun registerCallbacks(serviceId: Int, callbacks: OrdersCallbacks): Long =
        NetLiftHost.registerCallbacks(serviceId, CallbackForwarder(callbacks))

    override fun registerCallbacksV2(
        serviceId: Int,
        callbacks: OrdersCallbacksV2
    ): OrdersRegistrationV2 {
        val registration = NetLiftHost.registerCallbacksV2(
            serviceId,
            LifecycleV2CallbackForwarder(callbacks) { token -> v2Registrations.remove(token) }
        )
        v2Registrations.add(registration.token)
        return OrdersRegistrationV2(registration.token, registration.generation)
    }

    override fun revoke(registrationToken: Long) {
        if (registrationToken in v2Registrations)
            NetLiftHost.revokeV2(registrationToken)
        else
            NetLiftHost.revoke(registrationToken)
    }

    override fun revokeV2(registrationToken: Long) =
        NetLiftHost.revokeV2(registrationToken)

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

    override fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean) =
        NetLiftHost.reply(registrationToken, requestToken, accepted)

    override fun replyTyped(registrationToken: Long, requestToken: Long, reply: OrdersReply) =
        NetLiftHost.replyTyped(
            registrationToken,
            requestToken,
            NetLiftTypedReply(reply.outcome, reply.accepted, reply.diagnostic)
        )

    override fun ackDrain(registrationToken: Long, generation: Long) =
        NetLiftHost.ackDrain(registrationToken, generation)

    internal fun verifyLifecycleV2Cycles(serviceId: Int, cycles: Int) {
        require(cycles > 0)
        repeat(cycles) {
            val released = CountDownLatch(1)
            val failure = AtomicReference<Throwable?>()
            var expected: OrdersRegistrationV2? = null
            val callbacks = object : OrdersCallbacksV2 {
                override fun onRequest(
                    registrationToken: Long,
                    requestToken: Long,
                    methodId: Int,
                    payload: ByteArray
                ) = Unit

                override fun onResult(
                    operationToken: Long,
                    status: Int,
                    result: ByteArray,
                    error: ByteArray
                ) = Unit

                override fun onRelease(registrationToken: Long) {
                    if (registrationToken != expected?.token)
                        failure.compareAndSet(null, IllegalStateException("V2 release token mismatch."))
                    released.countDown()
                }

                override fun onRevoke(registrationToken: Long, generation: Long) {
                    val registration = expected
                    if (registration == null ||
                        registration.token != registrationToken ||
                        registration.generation != generation) {
                        failure.compareAndSet(null, IllegalStateException("V2 revoke identity mismatch."))
                        return
                    }
                    try {
                        ackDrain(registrationToken, generation)
                    } catch (error: Throwable) {
                        failure.compareAndSet(null, error)
                    }
                }
            }
            expected = registerCallbacksV2(serviceId, callbacks)
            NetLiftHost.revokeV2(expected!!.token)
            check(released.await(5, TimeUnit.SECONDS)) {
                "Lifecycle-v2 cycle did not deliver final release."
            }
            failure.get()?.let { throw IllegalStateException("Lifecycle-v2 cycle failed.", it) }
        }
    }

    private class CallbackForwarder(
        private val callbacks: OrdersCallbacks
    ) : NetLiftNativeCallbacks {
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

    private class LifecycleV2CallbackForwarder(
        private val callbacks: OrdersCallbacksV2,
        private val onReleased: (Long) -> Unit
    ) : NetLiftNativeCallbacksV2 {
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

        override fun onRelease(registrationToken: Long) {
            try {
                callbacks.onRelease(registrationToken)
            } finally {
                onReleased(registrationToken)
            }
        }

        override fun onRevoke(registrationToken: Long, generation: Long) =
            callbacks.onRevoke(registrationToken, generation)
    }
}
