package dev.netlift.runtime

import android.content.Context
import android.util.Base64
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import org.json.JSONObject

object NetLiftRuntime {
    private const val STALE_OPERATION_STATUS = 7

    private enum class State {
        Uninitialized,
        Initializing,
        Ready,
        Failed,
    }

    private var state = State.Uninitialized
    private var failure: Throwable? = null
    private val invocationScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val nextOperationToken = AtomicLong()

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

    fun registerNativeService(
        serviceId: UInt,
        handler: NetLiftNativeServiceHandler,
    ): NetLiftNativeServiceRegistration {
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u) { "NetLift service identifier must be non-zero." }
        val peer = NetLiftNativeServicePeer(handler)
        val handle = NetLiftNative.registerNativeService(serviceId.toInt(), peer)
        check(handle.size == 2) { "NetLift returned an invalid native-service handle." }
        return NetLiftNativeServiceRegistration(handle[0], handle[1], peer)
    }

    suspend fun invoke(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray = suspendCancellableCoroutine { continuation ->
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u && memberId != 0u) {
            "NetLift service and member identifiers must be non-zero."
        }
        val operationToken = newOperationToken()
        val completed = AtomicBoolean()
        val invocation = invocationScope.launch {
            continuation.resumeWith(
                runCatching {
                    decodeResponse(
                        NetLiftNative.invoke(
                            operationToken,
                            serviceId.toInt(),
                            memberId.toInt(),
                            payload,
                        )
                    )
                }
            )
        }
        invocation.invokeOnCompletion {
            completed.set(true)
        }
        continuation.invokeOnCancellation {
            invocation.cancel()
            invocationScope.launch {
                while (!completed.get()) {
                    if (NetLiftNative.cancel(operationToken) != STALE_OPERATION_STATUS)
                        break
                    delay(1)
                }
            }
        }
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
        val operationToken = newOperationToken()
        return decodeResponse(
            NetLiftNative.invokeOnAttachedThread(
                operationToken,
                serviceId.toInt(),
                memberId.toInt(),
                payload,
            )
        )
    }

    fun openStream(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): Flow<ByteArray> = flow {
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u && memberId != 0u) {
            "NetLift service and member identifiers must be non-zero."
        }
        val open = withContext(Dispatchers.IO) {
            JSONObject(
                NetLiftNative.openStream(
                    serviceId.toInt(),
                    memberId.toInt(),
                    payload,
                ).decodeToString()
            )
        }
        if (!open.getBoolean("success")) {
            throw NetLiftRemoteException(
                open.optInt("status", -1),
                open.optString("error", "Managed stream open failed."),
            )
        }
        val token = open.getLong("token")
        val generation = open.getLong("generation")
        try {
            while (true) {
                val response = readStream(token, generation)
                if (response.getBoolean("completed"))
                    break
                if (!response.getBoolean("success")) {
                    throw NetLiftRemoteException(
                        response.optInt("status", -1),
                        response.optString("error", "Managed stream read failed."),
                    )
                }
                emit(Base64.decode(response.getString("payload"), Base64.DEFAULT))
            }
        } finally {
            withContext(Dispatchers.IO) {
                NetLiftNative.revokeStream(token, generation)
            }
        }
    }

    private suspend fun readStream(token: Long, generation: Long): JSONObject =
        suspendCancellableCoroutine { continuation ->
            val read = invocationScope.launch {
                continuation.resumeWith(
                    runCatching {
                        JSONObject(
                            NetLiftNative.readStream(token, generation).decodeToString()
                        )
                    }
                )
            }
            continuation.invokeOnCancellation {
                read.cancel()
                invocationScope.launch {
                    NetLiftNative.revokeStream(token, generation)
                }
            }
        }

    private fun newOperationToken(): Long =
        nextOperationToken.updateAndGet { current ->
            if (current == Long.MAX_VALUE) 1L else current + 1L
        }

    private fun decodeResponse(payload: ByteArray): ByteArray {
        val response = JSONObject(payload.decodeToString())
        if (!response.getBoolean("success")) {
            throw NetLiftRemoteException(
                response.optInt("status", -1),
                response.optString("error", "Managed NetLift call failed."),
            )
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

class NetLiftRemoteException(
    val status: Int,
    val remoteMessage: String,
) : RuntimeException("NetLift status $status: $remoteMessage")

fun interface NetLiftNativeServiceHandler {
    suspend fun invoke(memberId: UInt, payload: ByteArray): ByteArray
}

class NetLiftNativeServiceRegistration internal constructor(
    val token: Long,
    val generation: Long,
    private val peer: NetLiftNativeServicePeer,
) : AutoCloseable {
    private val revoked = AtomicBoolean()

    fun revoke(): Int {
        if (!revoked.compareAndSet(false, true))
            return 6
        return NetLiftNative.revokeNativeService(token, generation)
    }

    override fun close() {
        revoke()
    }
}

class NetLiftNativeServicePeer internal constructor(
    private val handler: NetLiftNativeServiceHandler,
) {
    private val operations = ConcurrentHashMap<Long, Job>()

    fun invokeBlocking(
        operationToken: Long,
        memberId: Int,
        payload: ByteArray,
    ): ByteArray = runBlocking {
        val job = coroutineContext[Job]
            ?: error("NetLift native-service invocation has no coroutine job.")
        check(operations.putIfAbsent(operationToken, job) == null) {
            "NetLift native-service operation token $operationToken is already active."
        }
        try {
            handler.invoke(memberId.toUInt(), payload)
        } finally {
            operations.remove(operationToken, job)
        }
    }

    fun cancel(operationToken: Long): Int {
        val operation = operations[operationToken] ?: return 7
        operation.cancel()
        return 0
    }
}
