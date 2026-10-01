package com.netlift.androidhost

import __V5_CODEC_PACKAGE__.*
import com.netlift.androidruntime.v5.NegotiatedPeerContext

internal object CodecConsumer {
    fun encode(context: NegotiatedPeerContext): ByteArray =
        NetLiftV5DtoCodec.encodePayload(
            Payload(
                sku = "SKU-NEW",
                quantity = 42,
                note = "",
                nullableNote = null,
                giftWrap = false
            ),
            context
        )
}
