package dev.netlift.runtime

import android.content.Context
import android.util.Base64
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong

abstract class NetLiftNativeServiceDispatcher {
    private val activeCalls = ConcurrentHashMap<Long, Deferred<ByteArray>>()

    fun invoke(
        callToken: Long,
        serviceId: Int,
        memberId: Int,
        payload: ByteArray,
    ): ByteArray {
        require(callToken != 0L && serviceId != 0 && memberId != 0) {
            "Native callback call, service, and member identifiers must be non-zero."
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val call = scope.async(start = CoroutineStart.LAZY) {
            invokeAsync(serviceId.toUInt(), memberId.toUInt(), payload)
        }
        if (activeCalls.putIfAbsent(callToken, call) != null) {
            scope.cancel()
            throw IllegalStateException("Native callback call token $callToken is already active.")
        }
        call.start()
        return try {
            runBlocking { call.await() }
        } finally {
            activeCalls.remove(callToken, call)
            scope.cancel()
        }
    }

    fun cancel(callToken: Long) {
        activeCalls[callToken]?.cancel(
            CancellationException("Native callback call $callToken was cancelled."),
        )
    }

    protected abstract suspend fun invokeAsync(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray
}

class NetLiftNativeServiceRegistration internal constructor(
    val serviceId: UInt,
    val ownerToken: ULong,
    val token: ULong,
    val generation: ULong,
)

class NetLiftNativeServiceLease internal constructor(
    val registration: NetLiftNativeServiceRegistration,
) : AutoCloseable {
    private var closed = false

    @Synchronized
    override fun close() {
        if (closed) return
        NetLiftRuntime.revokeNativeService(registration)
        closed = true
    }
}

object NetLiftRuntime {
    private enum class State {
        Uninitialized,
        Initializing,
        Ready,
        Failed,
    }

    private var state = State.Uninitialized
    private var failure: Throwable? = null
    private val nextOperationToken = AtomicLong()
    private val nextNativeOwnerToken = AtomicLong()
    private val invocationExecutor = Executors.newCachedThreadPool { work ->
        Thread(work, "netlift-invocation").apply { isDaemon = true }
    }

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
        dispatcher: NetLiftNativeServiceDispatcher,
    ): NetLiftNativeServiceLease {
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u) { "NetLift native service identifiers must be non-zero." }
        val ownerToken = nextNativeOwnerToken.updateAndGet { current ->
            if (current == Long.MAX_VALUE) 1 else current + 1
        }.toULong()
        val response = try {
            JSONObject(NetLiftNative.registerNativeService(
                serviceId.toInt(),
                ownerToken.toLong(),
                dispatcher,
            ).decodeToString())
        } catch (error: Throwable) {
            val status = NetLiftNative.revokeNativeService(serviceId.toInt(), ownerToken.toLong())
            if (status != 0) {
                error.addSuppressed(
                    IllegalStateException("Native callback cleanup failed with status $status."),
                )
            }
            throw error
        }
        val registration = try {
            if (response.has("errorBase64")) {
                val message = Base64.decode(
                    response.getString("errorBase64"),
                    Base64.NO_WRAP,
                ).decodeToString()
                throw IllegalStateException(message)
            }
            NetLiftNativeServiceRegistration(
                serviceId,
                ownerToken,
                response.getString("token").toULong(),
                response.getString("generation").toULong(),
            ).also {
                require(it.token != 0uL && it.generation != 0uL) {
                    "Managed NetLift returned an invalid callback registration."
                }
            }
        } catch (error: Throwable) {
            val status = NetLiftNative.revokeNativeService(serviceId.toInt(), ownerToken.toLong())
            if (status != 0) {
                error.addSuppressed(
                    IllegalStateException("Native callback cleanup failed with status $status."),
                )
            }
            throw error
        }
        return NetLiftNativeServiceLease(registration)
    }

    fun closeNativeServices(leases: List<NetLiftNativeServiceLease>) {
        var failure: Throwable? = null
        leases.asReversed().forEach { lease ->
            try {
                lease.close()
            } catch (error: Throwable) {
                if (failure == null) failure = error else failure!!.addSuppressed(error)
            }
        }
        failure?.let { throw it }
    }

    internal fun revokeNativeService(registration: NetLiftNativeServiceRegistration) {
        check(isInitialized()) { "NetLift is not initialized." }
        val status = NetLiftNative.revokeNativeService(
            registration.serviceId.toInt(),
            registration.ownerToken.toLong(),
        )
        check(status == 0) {
            "NetLift native service revocation failed with status $status."
        }
    }

    suspend fun invoke(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): ByteArray {
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u && memberId != 0u) {
            "NetLift service and member identifiers must be non-zero."
        }
        val operationToken = nextOperationToken.updateAndGet { current ->
            if (current == Long.MAX_VALUE) 1 else current + 1
        }
        return suspendCancellableCoroutine { continuation ->
            continuation.invokeOnCancellation {
                NetLiftNative.cancel(operationToken)
            }
            invocationExecutor.execute {
                try {
                    val result = decodeResponse(
                        NetLiftNative.invoke(
                            operationToken,
                            serviceId.toInt(),
                            memberId.toInt(),
                            payload,
                        )
                    )
                    continuation.resumeWith(Result.success(result))
                } catch (error: Throwable) {
                    continuation.resumeWith(Result.failure(error))
                }
            }
        }
    }

    fun stream(
        serviceId: UInt,
        memberId: UInt,
        payload: ByteArray,
    ): Flow<ByteArray> = flow {
        check(isInitialized()) { "NetLift is not initialized." }
        require(serviceId != 0u && memberId != 0u) {
            "NetLift service and member identifiers must be non-zero."
        }
        val operationToken = nextOperationToken.updateAndGet { current ->
            if (current == Long.MAX_VALUE) 1 else current + 1
        }

        try {
            val opened = withContext(Dispatchers.IO) {
                decodeStreamResponse(
                    NetLiftNative.openStream(
                        operationToken,
                        serviceId.toInt(),
                        memberId.toInt(),
                        payload,
                    ),
                )
            }
            check(opened.success) {
                "NetLift status ${opened.status}: ${opened.error ?: "Managed stream open failed."}"
            }

            while (true) {
                val next = nextStream(operationToken)
                check(next.success) {
                    "NetLift status ${next.status}: ${next.error ?: "Managed stream failed."}"
                }
                if (!next.hasValue) break
                emit(Base64.decode(next.payload, Base64.DEFAULT))
            }
        } catch (error: CancellationException) {
            throw error
        } finally {
            withContext(NonCancellable + Dispatchers.IO) {
                NetLiftNative.cancel(operationToken)
                NetLiftNative.closeStream(operationToken)
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

    private suspend fun nextStream(operationToken: Long): StreamResponse =
        suspendCancellableCoroutine { continuation ->
            continuation.invokeOnCancellation {
                NetLiftNative.cancel(operationToken)
            }
            invocationExecutor.execute {
                try {
                    continuation.resumeWith(
                        Result.success(
                            decodeStreamResponse(NetLiftNative.nextStream(operationToken)),
                        ),
                    )
                } catch (error: Throwable) {
                    continuation.resumeWith(Result.failure(error))
                }
            }
        }

    private fun decodeStreamResponse(payload: ByteArray): StreamResponse {
        val response = JSONObject(payload.decodeToString())
        return StreamResponse(
            success = response.getBoolean("success"),
            hasValue = response.getBoolean("hasValue"),
            payload = response.optString("payload"),
            error = response.optString("error").takeIf(String::isNotEmpty),
            status = response.optInt("status", -1),
        )
    }

    private fun validateComponent(context: Context) {
        val manifest = context.assets.open("netlift-workload-component.json")
            .bufferedReader()
            .use { JSONObject(it.readText()) }
        require(manifest.getInt("schemaVersion") >= 1) {
            "Unsupported NetLift workload component manifest."
        }
        require(manifest.getInt("requiredRuntimeAbiVersion") == 2) {
            "The workload component requires an incompatible NetLift runtime ABI."
        }
        require(manifest.getString("requiredRuntimeVersion") == BuildConfig.NETLIFT_VERSION) {
            "The workload component requires a different NetLift runtime version."
        }
    }
}

private data class StreamResponse(
    val success: Boolean,
    val hasValue: Boolean,
    val payload: String,
    val error: String?,
    val status: Int,
)

data class NetLiftRuntimeDiagnostics(
    val globalReferenceCount: Int,
)
