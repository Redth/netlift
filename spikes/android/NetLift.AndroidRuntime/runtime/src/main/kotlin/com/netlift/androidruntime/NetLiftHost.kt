package com.netlift.androidruntime

import android.content.Context
import com.netlift.androidruntime.generated.RuntimeVariant
import org.json.JSONObject
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets

data class LiftContract(
    val assemblyName: String,
    val abiVersion: Int,
    val requiredFeatureBits: Int,
    val schemaHash: ByteArray,
    val ordersServiceId: Int,
    val placeOrderMethodId: Int,
    val confirmationServiceId: Int,
    val confirmationMethodId: Int,
    val managedAssetDirectory: String
)

object NetLiftHost {
    private const val MaxPayloadBytes = 4096
    private const val MaxDiagnosticBytes = 1024
    private const val SupportedFeatureBits = 0xF
    @Volatile private var initializedDescriptor: ByteArray? = null

    init {
        System.loadLibrary("netlift_androidruntime")
    }

    @Synchronized
    fun initialize(context: Context): LiftContract {
        val appId = context.packageName
        val appAssets = "netlift/apps/$appId"
        val contract = readContract(context, "$appAssets/contract.json")
        val expectedDescriptor = contract.descriptor()
        val previous = initializedDescriptor
        if (previous != null) {
            check(previous.contentEquals(expectedDescriptor)) {
                "A different managed contract is already initialized in this process."
            }
            return contract
        }

        val runtimeAssetDir =
            "netlift/runtime/${RuntimeVariant.runtimeVersion}/dotnet"
        val runtimeDir = File(
            context.filesDir,
            "netlift/runtime/${RuntimeVariant.runtimeVersion}/dotnet"
        )
        val managedDir = File(context.filesDir, contract.managedAssetDirectory)
        copyAssetTree(context, runtimeAssetDir, runtimeDir)
        copyAssetTree(context, contract.managedAssetDirectory, managedDir)

        val initialized = nativeInitialize(
            runtimeDir.path.encodeUtf8(),
            context.applicationInfo.nativeLibraryDir.encodeUtf8(),
            managedDir.path.encodeUtf8(),
            contract.assemblyName.encodeUtf8(),
            expectedDescriptor
        )
        check(initialized == 0) { "Generic CoreCLR host initialization failed: $initialized" }
        initializedDescriptor = expectedDescriptor
        return contract
    }

    fun start(context: Context, abiVersion: Int, schemaHash: ByteArray): NetLiftRuntimeInfo {
        val contract = initialize(context)
        if (contract.abiVersion != abiVersion)
            throw NativeAbiException(4, "ABI version mismatch.".encodeUtf8())
        if (!contract.schemaHash.contentEquals(schemaHash))
            throw NativeAbiException(5, "Contract schema hash mismatch.".encodeUtf8())
        if ((contract.requiredFeatureBits and SupportedFeatureBits) != contract.requiredFeatureBits)
            throw NativeAbiException(6, "Contract requires unsupported host features.".encodeUtf8())
        return NetLiftRuntimeInfo(
            contract.abiVersion,
            SupportedFeatureBits,
            contract.schemaHash.copyOf()
        )
    }

    fun registerCallbacks(serviceId: Int, callbacks: NetLiftNativeCallbacks): Long {
        require(serviceId > 0) { "Service ID must be positive." }
        check(initializedDescriptor != null) { "Call initialize() before registering callbacks." }
        return nativeRegisterCallbacks(serviceId, callbacks)
    }

    fun registerCallbacksV2(
        serviceId: Int,
        callbacks: NetLiftNativeCallbacksV2
    ): NetLiftNativeRegistrationV2 {
        require(serviceId > 0) { "Service ID must be positive." }
        check(initializedDescriptor != null) { "Call initialize() before registering callbacks." }
        val registration = nativeRegisterCallbacksV2(serviceId, callbacks)
        require(registration.size == 2 && registration[0] != 0L && registration[1] != 0L) {
            "Managed host returned an invalid V2 callback registration."
        }
        return NetLiftNativeRegistrationV2(registration[0], registration[1])
    }

    fun revoke(registrationToken: Long) {
        require(registrationToken != 0L) { "Registration token must be non-zero." }
        nativeRevoke(registrationToken)
    }

    fun revokeV2(registrationToken: Long) {
        require(registrationToken != 0L) { "Registration token must be non-zero." }
        nativeRevokeV2(registrationToken)
    }

    fun ackDrain(registrationToken: Long, generation: Long) {
        require(registrationToken != 0L && generation != 0L) {
            "Registration token and generation must be non-zero."
        }
        nativeAckDrainV2(registrationToken, generation)
    }

    fun begin(
        serviceId: Int,
        methodId: Int,
        registrationToken: Long,
        arguments: ByteArray
    ): Long {
        require(serviceId > 0 && methodId > 0) { "Route IDs must be positive." }
        require(registrationToken != 0L) { "Registration token must be non-zero." }
        require(arguments.size <= MaxPayloadBytes) { "Payload exceeds the 4096-byte limit." }
        check(initializedDescriptor != null) { "Call initialize() before beginning an operation." }
        return nativeBegin(serviceId, methodId, registrationToken, arguments)
    }

    fun activate(operationToken: Long) {
        require(operationToken != 0L) { "Operation token must be non-zero." }
        nativeActivate(operationToken)
    }

    fun cancel(operationToken: Long) {
        require(operationToken != 0L) { "Operation token must be non-zero." }
        nativeCancel(operationToken)
    }

    fun reply(registrationToken: Long, requestToken: Long, accepted: Boolean) {
        require(registrationToken != 0L && requestToken != 0L) {
            "Registration and request tokens must be non-zero."
        }
        nativeReply(registrationToken, requestToken, accepted)
    }

    fun replyTyped(registrationToken: Long, requestToken: Long, reply: NetLiftTypedReply) {
        require(registrationToken != 0L && requestToken != 0L) {
            "Registration and request tokens must be non-zero."
        }
        require(reply.outcome in 0..5) { "Typed native reply outcome is invalid." }
        require(reply.outcome == 0 || !reply.accepted) {
            "Only RESULT may contain an accepted value."
        }
        nativeReplyTyped(
            registrationToken,
            requestToken,
            reply.outcome,
            if (reply.accepted) 1 else 0,
            boundedDiagnostic(reply.diagnostic)
        )
    }

    fun invoke(serviceId: Int, methodId: Int, payload: ByteArray): ByteArray {
        require(serviceId > 0 && methodId > 0) { "Route IDs must be positive." }
        require(payload.size <= MaxPayloadBytes) { "Payload exceeds the 4096-byte limit." }
        check(initializedDescriptor != null) { "Call initialize() before invoking a route." }

        val envelope = nativeInvoke(serviceId, methodId, payload)
        require(envelope.size >= 12) { "Managed host returned a malformed response envelope." }
        val input = ByteBuffer.wrap(envelope).order(ByteOrder.LITTLE_ENDIAN)
        val status = input.int
        val resultLength = input.int
        val errorLength = input.int
        require(resultLength >= 0 && errorLength >= 0 &&
            resultLength.toLong() + errorLength.toLong() == input.remaining().toLong()
        ) { "Managed host returned inconsistent response lengths." }
        val result = ByteArray(resultLength)
        input.get(result)
        val errorBytes = ByteArray(errorLength)
        input.get(errorBytes)
        if (status != 0) {
            val message = StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(errorBytes))
                .toString()
            throw IllegalStateException("Managed route failed with status $status: $message")
        }
        check(errorLength == 0) { "Successful managed response included an error message." }
        return result
    }

    fun subscribeStream(
        serviceId: Int,
        subscriptionId: Int,
        arguments: ByteArray,
        callbacks: NetLiftStreamCallbacks,
        initialCredit: Int,
        transportCapacity: Int,
        maxItemBytes: Int
    ): Long {
        require(serviceId > 0 && subscriptionId > 0) { "Stream route IDs must be positive." }
        require(arguments.size <= MaxPayloadBytes) { "Stream payload exceeds the 4096-byte limit." }
        require(initialCredit == 0) { "Stream subscriptions must start with zero credit." }
        require(transportCapacity == 64) { "Stream transport capacity is unsupported." }
        require(maxItemBytes in 1..MaxPayloadBytes) { "Stream item limit is invalid." }
        check(initializedDescriptor != null) { "Call initialize() before subscribing to a stream." }
        return nativeSubscribeStream(
            serviceId,
            subscriptionId,
            arguments,
            callbacks,
            initialCredit,
            transportCapacity,
            maxItemBytes
        )
    }

    fun activateStream(token: Long) {
        require(token != 0L) { "Stream token must be non-zero." }
        nativeActivateStream(token)
    }

    fun requestStream(token: Long, credits: Int) {
        require(token != 0L) { "Stream token must be non-zero." }
        require(credits == 1) { "Stream demand must be requested one item at a time." }
        nativeRequestStream(token, credits)
    }

    fun unsubscribeStream(token: Long) {
        require(token != 0L) { "Stream token must be non-zero." }
        nativeUnsubscribeStream(token)
    }

    private fun readContract(context: Context, assetPath: String): LiftContract {
        val json = context.assets.open(assetPath).bufferedReader(Charsets.UTF_8).use { it.readText() }
        val value = JSONObject(json)
        val hashHex = value.getString("schemaHash")
        require(hashHex.length == 64 && hashHex.all { it.digitToIntOrNull(16) != null }) {
            "Contract schemaHash must contain exactly 32 hexadecimal bytes."
        }
        val managedAssetDirectory = value.getString("managedAssetDirectory")
        require(managedAssetDirectory.startsWith("netlift/apps/") &&
            !managedAssetDirectory.split('/').any { it == "." || it == ".." }
        ) { "Contract managedAssetDirectory is outside the app-specific asset namespace." }
        return LiftContract(
            assemblyName = value.getString("assemblyName"),
            abiVersion = value.getInt("abiVersion"),
            requiredFeatureBits = value.getInt("requiredFeatureBits"),
            schemaHash = hashHex.chunked(2).map { it.toInt(16).toByte() }.toByteArray(),
            ordersServiceId = value.getInt("ordersServiceId"),
            placeOrderMethodId = value.getInt("placeOrderMethodId"),
            confirmationServiceId = value.getInt("confirmationServiceId"),
            confirmationMethodId = value.getInt("confirmationMethodId"),
            managedAssetDirectory = managedAssetDirectory
        )
    }

    private fun LiftContract.descriptor(): ByteArray =
        ByteBuffer.allocate(48)
            .order(ByteOrder.LITTLE_ENDIAN)
            .putInt(48)
            .putInt(1)
            .putInt(abiVersion)
            .putInt(requiredFeatureBits)
            .put(schemaHash)
            .array()

    private fun copyAssetTree(context: Context, assetPath: String, target: File) {
        val children = context.assets.list(assetPath)
            ?: throw IllegalStateException("Missing packaged managed assets at $assetPath.")
        check(children.isNotEmpty()) { "Packaged managed asset directory is empty: $assetPath." }
        target.mkdirs()
        for (child in children) {
            require(child != "." && child != ".." && !child.contains('/')) {
                "Invalid path entry in packaged assets."
            }
            val childAsset = "$assetPath/$child"
            val childTarget = File(target, child)
            val nested = context.assets.list(childAsset).orEmpty()
            if (nested.isNotEmpty()) {
                copyAssetTree(context, childAsset, childTarget)
            } else {
                context.assets.open(childAsset).use { input ->
                    childTarget.outputStream().use { output -> input.copyTo(output) }
                }
            }
        }
    }

    private fun boundedDiagnostic(value: ByteArray): ByteArray {
        val fallback = "Handler failure diagnostic unavailable or too large."
            .toByteArray(StandardCharsets.US_ASCII)
        if (value.size > MaxDiagnosticBytes)
            return fallback
        return try {
            StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(value))
            value.copyOf()
        } catch (_: Exception) {
            fallback
        }
    }

    private fun String.encodeUtf8(): ByteArray =
        toByteArray(StandardCharsets.UTF_8)

    private external fun nativeInitialize(
        runtimeDirectoryUtf8: ByteArray,
        nativeLibraryDirectoryUtf8: ByteArray,
        managedDirectoryUtf8: ByteArray,
        contractAssemblyUtf8: ByteArray,
        expectedDescriptor: ByteArray
    ): Int

    private external fun nativeInvoke(
        serviceId: Int,
        methodId: Int,
        payload: ByteArray
    ): ByteArray

    private external fun nativeRegisterCallbacks(
        serviceId: Int,
        callbacks: NetLiftNativeCallbacks
    ): Long

    private external fun nativeRegisterCallbacksV2(
        serviceId: Int,
        callbacks: NetLiftNativeCallbacksV2
    ): LongArray

    private external fun nativeRevoke(registrationToken: Long)

    private external fun nativeRevokeV2(registrationToken: Long)

    private external fun nativeAckDrainV2(registrationToken: Long, generation: Long)

    private external fun nativeBegin(
        serviceId: Int,
        methodId: Int,
        registrationToken: Long,
        arguments: ByteArray
    ): Long

    private external fun nativeActivate(operationToken: Long)

    private external fun nativeCancel(operationToken: Long)

    private external fun nativeReply(
        registrationToken: Long,
        requestToken: Long,
        accepted: Boolean
    )

    private external fun nativeReplyTyped(
        registrationToken: Long,
        requestToken: Long,
        outcome: Int,
        accepted: Int,
        diagnostic: ByteArray
    )

    private external fun nativeSubscribeStream(
        serviceId: Int,
        subscriptionId: Int,
        arguments: ByteArray,
        callbacks: NetLiftStreamCallbacks,
        initialCredit: Int,
        transportCapacity: Int,
        maxItemBytes: Int
    ): Long

    private external fun nativeActivateStream(token: Long)

    private external fun nativeRequestStream(token: Long, credits: Int)

    private external fun nativeUnsubscribeStream(token: Long)
}
