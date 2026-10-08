package dev.netlift.runtime

import android.content.Context
import android.util.Base64
import org.json.JSONObject

object NetLiftRuntime {
    private enum class State {
        Uninitialized,
        Initializing,
        Ready,
        Failed,
    }

    private var state = State.Uninitialized
    private var failure: Throwable? = null

    init {
        System.loadLibrary("netlift_android")
    }

    @JvmStatic
    @Synchronized
    fun initialize(context: Context) {
        when (state) {
            State.Ready -> return
            State.Failed -> throw IllegalStateException(
                "NetLift previously failed to initialize.",
                failure,
            )
            State.Initializing -> error("Reentrant NetLift initialization is not supported.")
            State.Uninitialized -> Unit
        }

        state = State.Initializing
        try {
            val applicationContext = context.applicationContext ?: context
            validateComponent(applicationContext)
            DotnetBootstrap.initialize(applicationContext)
            NetLiftNative.attach(NetLiftRuntime::class.java.classLoader!!)
            state = State.Ready
        } catch (error: Throwable) {
            failure = error
            state = State.Failed
            throw error
        }
    }

    @JvmStatic
    @Synchronized
    fun isInitialized(): Boolean = state == State.Ready

    @JvmStatic
    @Synchronized
    fun initializationFailure(): Throwable? = failure

    @JvmStatic
    fun diagnostics(): NetLiftRuntimeDiagnostics =
        NetLiftRuntimeDiagnostics(NetLiftNative.globalReferenceCount())

    suspend fun invoke(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray {
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u && memberId != 0u) {
            "NetLift service and member identifiers must be non-zero."
        }
        return decodeResponse(
            NetLiftNative.invoke(serviceId.toInt(), memberId.toInt(), payload)
        )
    }

    fun invokeFromNativeThreadForDiagnostics(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray {
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u && memberId != 0u) {
            "NetLift service and member identifiers must be non-zero."
        }
        return decodeResponse(
            NetLiftNative.invokeOnAttachedThread(
                serviceId.toInt(),
                memberId.toInt(),
                payload,
            )
        )
    }

    private fun decodeResponse(payload: ByteArray): ByteArray {
        val response = JSONObject(payload.decodeToString())
        check(response.getBoolean("success")) {
            "NetLift status ${response.optInt("status", -1)}: " +
                response.optString("error", "Managed NetLift call failed.")
        }
        return Base64.decode(response.getString("payload"), Base64.DEFAULT)
    }

    private fun validateComponent(context: Context) {
        val manifest = context.assets.open("netlift-workload-component.json")
            .bufferedReader()
            .use { JSONObject(it.readText()) }
        require(manifest.getInt("schemaVersion") >= 1) {
            "Unsupported NetLift workload component manifest."
        }
        require(manifest.getInt("requiredRuntimeAbiVersion") == 1) {
            "The workload component requires an incompatible NetLift runtime ABI."
        }
        require(manifest.getString("requiredRuntimeVersion") == BuildConfig.NETLIFT_VERSION) {
            "The workload component requires a different NetLift runtime version."
        }
    }
}

data class NetLiftRuntimeDiagnostics(
    val globalReferenceCount: Int,
)
