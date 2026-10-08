package dev.netlift.workloadlibrarymode.app

import dev.netlift.orders.NetLiftTransport
import dev.netlift.runtime.NetLiftRuntime

internal class ManagedNetLiftTransport : NetLiftTransport {
    override suspend fun invoke(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray {
        return NetLiftRuntime.invoke(serviceId, memberId, payload)
    }
}
