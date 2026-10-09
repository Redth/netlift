package dev.netlift.workloadlibrarymode.app

import dev.netlift.orders.NetLiftTransport
import dev.netlift.orders.NetLiftStreamTransport
import dev.netlift.runtime.NetLiftRuntime
import kotlinx.coroutines.flow.Flow

internal class ManagedNetLiftTransport : NetLiftTransport, NetLiftStreamTransport {
    override suspend fun invoke(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray {
        return NetLiftRuntime.invoke(serviceId, memberId, payload)
    }

    override fun openStream(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): Flow<ByteArray> = NetLiftRuntime.openStream(serviceId, memberId, payload)
}
