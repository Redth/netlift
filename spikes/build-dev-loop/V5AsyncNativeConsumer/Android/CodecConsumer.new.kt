package com.netlift.androidhost

import com.netlift.androidhost.contract.*
import com.netlift.androidruntime.v5.ManagedResultSchemaContext
import com.netlift.androidruntime.v5.NegotiatedPeerContext

internal object AsyncCodecConsumer {
    fun encodeRequest(
        value: SubmitInput,
        context: NegotiatedPeerContext
    ): ByteArray = NetLiftV5AsyncCodec.encodeRequest(value, context)

    fun decodeResult(
        source: ByteArray,
        context: ManagedResultSchemaContext
    ): SubmitResult = NetLiftV5AsyncCodec.decodeResult(source, context)
}
