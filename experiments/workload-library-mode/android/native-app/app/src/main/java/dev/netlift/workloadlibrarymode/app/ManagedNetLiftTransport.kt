package dev.netlift.workloadlibrarymode.app

import android.util.Base64
import dev.netlift.orders.NetLiftTransport
import dev.netlift.workloadlibrarymode.ManagedProofBridge
import org.json.JSONObject

internal class ManagedNetLiftTransport(
    private val bridge: ManagedProofBridge = ManagedProofBridge(),
) : NetLiftTransport {
    override suspend fun invoke(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray {
        val response = JSONObject(
            bridge.invoke(
            serviceId.toInt(),
            memberId.toInt(),
            payload.decodeToString(),
            )
        )
        check(response.getBoolean("success")) {
            response.optString("error", "Managed NetLift call failed.")
        }
        return Base64.decode(response.getString("payload"), Base64.DEFAULT)
    }
}
