package dev.netlift.workloadlibrarymode.app

import android.util.Base64
import dev.netlift.orders.NetLiftTransport
import dev.netlift.workloadlibrarymode.ManagedNetLiftBridge
import org.json.JSONObject

internal class ManagedNetLiftTransport(
    private val bridge: ManagedNetLiftBridge = ManagedNetLiftBridge(),
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
            "NetLift status ${response.optInt("status", -1)}: " +
                response.optString("error", "Managed NetLift call failed.")
        }
        return Base64.decode(response.getString("payload"), Base64.DEFAULT)
    }
}
