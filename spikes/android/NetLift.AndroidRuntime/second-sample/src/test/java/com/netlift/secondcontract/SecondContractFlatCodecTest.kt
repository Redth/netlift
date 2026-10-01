package com.netlift.secondcontract

import com.netlift.androidhost.contract.DispatchReceipt
import com.netlift.androidhost.contract.DispatchRequest
import com.netlift.androidhost.contract.NetLiftFlatDtoWire
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

internal class SecondContractFlatCodecTest {
    @Test
    fun generatedRequestEncoderPreservesTheExistingWireBytes() {
        val request = DispatchRequest("north-star", 73)
        val expected = byteArrayOf(1, 0, 10, 0, 0, 0) +
            "north-star".toByteArray(Charsets.UTF_8) +
            byteArrayOf(2, 0, 8, 0, 0, 0, 73, 0, 0, 0, 0, 0, 0, 0)

        assertArrayEquals(expected, NetLiftFlatDtoWire.encodeDispatchRequest(request))
    }

    @Test
    fun generatedReceiptDecoderPreservesTheExistingWireBytes() {
        val expected = byteArrayOf(1, 0, 8, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0) +
            byteArrayOf(2, 0, 20, 0, 0, 0) +
            "delivered:north-star".toByteArray(Charsets.UTF_8) +
            byteArrayOf(3, 0, 8, 0, 0, 0, 73, 0, 0, 0, 0, 0, 0, 0) +
            byteArrayOf(4, 0, 1, 0, 0, 0, 1)
        val receipt = DispatchReceipt(
            deliveryId = 1,
            channel = "delivered:north-star",
            sequence = 73,
            approved = true
        )

        assertEquals(receipt, NetLiftFlatDtoWire.decodeDispatchReceipt(expected))
        assertArrayEquals(expected, NetLiftFlatDtoWire.encodeDispatchReceipt(receipt))
    }
}
