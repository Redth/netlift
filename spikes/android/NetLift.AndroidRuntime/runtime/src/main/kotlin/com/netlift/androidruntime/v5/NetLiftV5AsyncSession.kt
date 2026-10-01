package com.netlift.androidruntime.v5

import com.netlift.androidruntime.NetLiftNativeRegistrationV2
import com.netlift.androidruntime.NetLiftTypedReply

public class NetLiftV5AsyncSession internal constructor(
    private val endpoint: NetLiftV5Endpoint,
    private val endpointToken: Long,
    private val peerContext: NegotiatedPeerContext,
    private val resultContext: ManagedResultSchemaContext,
    public val registration: NetLiftNativeRegistrationV2
) : AutoCloseable {
    @Volatile
    private var unbound = false
    @Volatile
    private var drainAcknowledgementReturned = false

    public fun begin(payload: ByteArray): Long {
        requireOpen()
        peerContext.requireActive()
        resultContext.requireActive()
        require(payload.isNotEmpty() && payload.size <= MAX_ASYNC_PAYLOAD_BYTES) {
            "AsyncRuntime V2 request length is outside supported bounds."
        }
        return NativeV5Api.nativeBeginAsyncV2(
            endpointToken,
            registration.token,
            registration.generation,
            peerContext.serviceId.toInt(),
            peerContext.methodId.toInt(),
            payload.copyOf()
        )
    }

    public fun activate(operationToken: Long) {
        requireToken(operationToken, "operation")
        requireOpen()
        NativeV5Api.nativeActivateAsyncV2(
            endpointToken,
            registration.token,
            registration.generation,
            operationToken
        )
    }

    public fun cancel(operationToken: Long) {
        requireToken(operationToken, "operation")
        requireOpen()
        NativeV5Api.nativeCancelAsyncV2(
            endpointToken,
            registration.token,
            registration.generation,
            operationToken
        )
    }

    public fun cancelRequest(requestToken: Long) {
        requireToken(requestToken, "request")
        requireOpen()
        NativeV5Api.nativeCancelRequestAsyncV2(
            endpointToken,
            registration.token,
            registration.generation,
            requestToken
        )
    }

    public fun replyTyped(requestToken: Long, reply: NetLiftTypedReply) {
        requireToken(requestToken, "request")
        requireOpen()
        require(reply.diagnostic.size <= MAX_DIAGNOSTIC_BYTES) {
            "Typed-reply diagnostic exceeds the supported bound."
        }
        NativeV5Api.nativeReplyTypedAsyncV2(
            endpointToken,
            registration.token,
            registration.generation,
            requestToken,
            reply.outcome,
            reply.accepted,
            reply.diagnostic.copyOf()
        )
    }

    public fun replyConfirmation(requestToken: Long, accepted: Boolean) =
        replyTyped(
            requestToken,
            NetLiftTypedReply(RESULT_REPLY, accepted)
        )

    public fun revoke() {
        requireOpen()
        NativeV5Api.nativeRevokeAsyncV2(
            endpointToken,
            registration.token,
            registration.generation
        )
    }

    public fun acknowledgeDrain() {
        requireOpen()
        NativeV5Api.nativeAckDrainAsyncV2(
            endpointToken,
            registration.token,
            registration.generation
        )
        drainAcknowledgementReturned = true
    }

    public fun leaseSnapshot(): LongArray {
        val snapshot = NativeV5Api.nativeAsyncV2LeaseSnapshot()
            ?: throw IllegalStateException("Native V2 lease snapshot was unavailable.")
        check(snapshot.size == LEASE_SNAPSHOT_SIZE) {
            "Native V2 lease snapshot has an invalid shape."
        }
        return snapshot.copyOf()
    }

    override fun close() {
        synchronized(this) {
            if (unbound)
                return
            check(drainAcknowledgementReturned) {
                "Acknowledge the completed V2 drain before unbinding the endpoint."
            }
            NativeV5Api.nativeUnbindAsyncV2(endpointToken)
            unbound = true
        }
        endpoint.releaseAsyncV2Session(this)
    }

    internal val isUnbound: Boolean
        get() = unbound

    private fun requireOpen() {
        check(!unbound) { "The AsyncRuntime V2 session is unbound." }
    }

    private fun requireToken(value: Long, kind: String) {
        require(value > 0) { "The AsyncRuntime V2 $kind token must be positive." }
    }

    private companion object {
        const val MAX_ASYNC_PAYLOAD_BYTES = 4096
        const val MAX_DIAGNOSTIC_BYTES = 4096
        const val LEASE_SNAPSHOT_SIZE = 5
        const val RESULT_REPLY = 0
    }
}
