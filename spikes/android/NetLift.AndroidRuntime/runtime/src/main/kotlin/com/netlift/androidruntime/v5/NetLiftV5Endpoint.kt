package com.netlift.androidruntime.v5

import android.content.Context
import com.netlift.androidruntime.NativeAbiException
import com.netlift.androidruntime.generated.RuntimeVariant
import org.json.JSONObject
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets
import java.security.MessageDigest

public class CompatibleStartRequest(
    public val peerManifestJson: String,
    public val trustedPeerHashHex: String,
    public val trustedMinimumMajor: Int,
    public val trustedMinimumMinor: Int,
    public val peerOptIn: Boolean
)

public data class V5RouteCounters(
    public val factoryCalls: Long,
    public val routeCalls: Long,
    public val lastQuantity: Long,
    public val optionalDefaultsValid: Boolean
)

public class NetLiftV5Endpoint private constructor(
    private val endpointToken: Long,
    private val managedSchemaHashHex: String,
    private val managedManifestIdentity: ManagedV5ManifestIdentity,
    private val defaultStartRequest: CompatibleStartRequest,
    private val peerContextLease: PeerContextLease
) : AutoCloseable {
    private val gate = Any()
    private var startAttempted = false
    private var started = false
    private var closed = false
    private var issuedPeerContext: NegotiatedPeerContext? = null
    private var issuedManagedResultSchemaContext: ManagedResultSchemaContext? = null
    private var issuedPeerSchemaSnapshot: ByteArray? = null
    private var issuedManagedResultSchemaSnapshot: ByteArray? = null
    private var managedResultSchemaLease: ManagedResultSchemaLease? = null
    private var asyncV2Session: NetLiftV5AsyncSession? = null

    public val peerContext: NegotiatedPeerContext?
        get() = synchronized(gate) { issuedPeerContext }

    public val managedResultSchemaContext: ManagedResultSchemaContext?
        get() = synchronized(gate) { issuedManagedResultSchemaContext }

    public fun start(expectedManagedSchemaHashHex: String): NegotiatedPeerContext =
        start(defaultStartRequest, expectedManagedSchemaHashHex)

    public fun start(
        request: CompatibleStartRequest,
        expectedManagedSchemaHashHex: String
    ): NegotiatedPeerContext = synchronized(gate) {
        check(!closed) { "The v5 endpoint is closed." }
        if (startAttempted)
            throw NativeAbiException(
                START_ALREADY_ATTEMPTED,
                "Compatible-start endpoint permits only one start attempt."
                    .toByteArray(StandardCharsets.UTF_8)
            )
        startAttempted = true

        require(expectedManagedSchemaHashHex.isSha256Hex()) {
            "Expected managed schema hash must contain exactly 32 hexadecimal bytes."
        }
        val expectedManagedHash = expectedManagedSchemaHashHex.lowercase()
        require(MessageDigest.isEqual(
            managedSchemaHashHex.hexToBytes(),
            expectedManagedHash.hexToBytes()
        )) {
            "Expected managed schema hash does not match the managed GetManifest hash."
        }

        val peer = parseV5PeerManifest(request.peerManifestJson)
        require(request.trustedMinimumMajor > 0 && request.trustedMinimumMinor >= 0) {
            "Peer trust minimum version is invalid."
        }
        require(request.trustedPeerHashHex.isSha256Hex()) {
            "Trusted peer hash must contain exactly 32 hexadecimal bytes."
        }
        val trustedHashHex = request.trustedPeerHashHex.lowercase()
        require(MessageDigest.isEqual(
            peer.schemaHashHex.hexToBytes(),
            trustedHashHex.hexToBytes()
        )) {
            "Trusted peer hash does not match the peer manifest full-schema hash."
        }

        val peerManifestBytes = request.peerManifestJson.toByteArray(StandardCharsets.UTF_8)
        require(peerManifestBytes.isNotEmpty() && peerManifestBytes.size <= MAX_MANIFEST_BYTES) {
            "Peer manifest exceeds the compatible-start size limit."
        }
        val hello = encodePeerHello(peer)
        var managedStartSucceeded = false
        try {
            NativeV5Api.nativeStart(
                endpointToken,
                peerManifestBytes,
                hello,
                trustedHashHex.hexToBytes(),
                request.trustedMinimumMajor,
                request.trustedMinimumMinor,
                request.peerOptIn
            )
            managedStartSucceeded = true

            val snapshot = NativeV5Api.nativeGetPeerSchema(endpointToken)
                ?: throw IllegalStateException("Managed v5 host returned no NLPS snapshot.")
            issuedPeerSchemaSnapshot = snapshot.copyOf()
            val context = NegotiatedPeerContext.issue(
                snapshot,
                managedSchemaHashHex,
                peer,
                peerContextLease
            )
            val resultContext = managedManifestIdentity.resultRoute?.let { route ->
                acquireManagedResultSchemaContext(peer, route)
            }
            issuedPeerContext = context
            issuedManagedResultSchemaContext = resultContext
            started = true
            context
        } catch (failure: Exception) {
            if (managedStartSucceeded) {
                closed = true
                peerContextLease.invalidate()
                managedResultSchemaLease?.invalidate()
                issuedPeerSchemaSnapshot = null
                issuedManagedResultSchemaSnapshot = null
                try {
                    NativeV5Api.nativeCloseEndpoint(endpointToken)
                } catch (closeFailure: Exception) {
                    failure.addSuppressed(closeFailure)
                }
            }
            throw failure
        }
    }

    public fun dispatch(payload: ByteArray): Int = synchronized(gate) {
        check(!closed) { "The v5 endpoint is closed." }
        require(payload.isNotEmpty() && payload.size <= MAX_DTO_BYTES) {
            "v5 DTO payload length is outside supported bounds."
        }
        NativeV5Api.nativeDispatch(endpointToken, payload.copyOf())
    }

    public fun dispatch(context: NegotiatedPeerContext, payload: ByteArray): Int =
        synchronized(gate) {
            check(!closed) { "The v5 endpoint is closed." }
            context.requireActive()
            check(context === issuedPeerContext && started) {
                "Negotiated peer context does not belong to this started endpoint."
            }
            require(payload.isNotEmpty() && payload.size <= MAX_DTO_BYTES) {
                "v5 DTO payload length is outside supported bounds."
            }
            NativeV5Api.nativeDispatch(endpointToken, payload.copyOf())
        }

    public fun routeCounters(): V5RouteCounters = synchronized(gate) {
        check(!closed) { "The v5 endpoint is closed." }
        val values = NativeV5Api.nativeGetCounters()
        check(values != null && values.size == COUNTER_COUNT) {
            "Managed v5 route counter snapshot is malformed."
        }
        V5RouteCounters(
            factoryCalls = values[0],
            routeCalls = values[1],
            lastQuantity = values[2],
            optionalDefaultsValid = values[3] == 1L
        )
    }

    public fun openAsyncV2Session(
        callbacks: com.netlift.androidruntime.NetLiftNativeCallbacksV2
    ): NetLiftV5AsyncSession = synchronized(gate) {
        check(!closed && started) {
            "AsyncRuntime V2 requires a started, open v5 endpoint."
        }
        check(asyncV2Session == null) {
            "This v5 endpoint already owns an AsyncRuntime V2 session."
        }
        val peer = issuedPeerContext
            ?: throw IllegalStateException("The v5 endpoint has no negotiated peer context.")
        val resultContext = issuedManagedResultSchemaContext
            ?: throw IllegalStateException("The v5 endpoint has no managed-result context.")
        peer.requireActive()
        resultContext.requireActive()
        val peerSnapshot = issuedPeerSchemaSnapshot
            ?: throw IllegalStateException("The v5 endpoint has no issued NLPS snapshot.")
        val resultSnapshot = issuedManagedResultSchemaSnapshot
            ?: throw IllegalStateException("The v5 endpoint has no issued NLMR snapshot.")
        val identity = NativeV5Api.nativeBindAsyncV2(
            endpointToken,
            peerSnapshot.copyOf(),
            resultSnapshot.copyOf(),
            managedSchemaHashHex.hexToBytes(),
            callbacks
        ) ?: throw IllegalStateException("Native V2 binding returned no registration identity.")
        check(identity.size == 2 && identity[0] > 0 && identity[1] > 0) {
            "Native V2 binding returned an invalid registration identity."
        }
        NetLiftV5AsyncSession(
            this,
            endpointToken,
            peer,
            resultContext,
            com.netlift.androidruntime.NetLiftNativeRegistrationV2(identity[0], identity[1])
        ).also { asyncV2Session = it }
    }

    override fun close() {
        synchronized(gate) {
            if (closed)
                return
            check(asyncV2Session == null) {
                "Drain and unbind the AsyncRuntime V2 session before closing its endpoint."
            }
            closed = true
            peerContextLease.invalidate()
            managedResultSchemaLease?.invalidate()
            issuedPeerSchemaSnapshot = null
            issuedManagedResultSchemaSnapshot = null
            NativeV5Api.nativeCloseEndpoint(endpointToken)
        }
    }

    internal fun releaseAsyncV2Session(session: NetLiftV5AsyncSession) {
        synchronized(gate) {
            check(asyncV2Session === session) {
                "AsyncRuntime V2 session does not belong to this endpoint."
            }
            asyncV2Session = null
        }
    }

    private fun acquireManagedResultSchemaContext(
        peer: V5PeerSchemaIdentity,
        route: ManagedResultRouteIdentity
    ): ManagedResultSchemaContext {
        require(peer.serviceId == route.serviceId && peer.methodId == route.methodId) {
            "Managed result route differs from the compatible-start route."
        }
        val lease = ManagedResultSchemaLease.create(
            endpointToken,
            route.profileId,
            route.serviceId,
            route.methodId,
            route.resultDtoId,
            route.resultDtoVersion
        )
        managedResultSchemaLease = lease

        val shortBuffer = ByteArray(MANAGED_RESULT_SCHEMA_HEADER_SIZE) {
            SHORT_BUFFER_SENTINEL
        }
        val preflight = NativeV5Api.nativeGetManagedResultSchema(
            endpointToken,
            checkedRouteId(route.serviceId),
            checkedRouteId(route.methodId),
            shortBuffer
        ) ?: throw IllegalStateException("Managed v5 host returned no NLMR size result.")
        check(preflight.size == RESULT_SCHEMA_COUNTS) {
            "Managed v5 host returned malformed NLMR size counts."
        }
        val requiredBytes = preflight[2]
        check(preflight[0] == SCHEMA_BUFFER_TOO_SMALL.toLong() &&
            preflight[1] == 0L &&
            requiredBytes == (MANAGED_RESULT_SCHEMA_HEADER_SIZE +
                route.fields.size * MANAGED_RESULT_SCHEMA_FIELD_SIZE).toLong() &&
            requiredBytes <= MANAGED_RESULT_SCHEMA_MAX_SIZE &&
            shortBuffer.all { it == SHORT_BUFFER_SENTINEL }
        ) { "Managed v5 host violated the NLMR caller-buffer preflight contract." }

        val snapshot = ByteArray(requiredBytes.toInt())
        val acquired = NativeV5Api.nativeGetManagedResultSchema(
            endpointToken,
            checkedRouteId(route.serviceId),
            checkedRouteId(route.methodId),
            snapshot
        ) ?: throw IllegalStateException("Managed v5 host returned no NLMR snapshot result.")
        check(acquired.size == RESULT_SCHEMA_COUNTS &&
            acquired[0] == OK.toLong() &&
            acquired[1] == requiredBytes &&
            acquired[2] == requiredBytes
        ) { "Managed v5 host failed to fill the exact NLMR caller buffer." }
        issuedManagedResultSchemaSnapshot = snapshot.copyOf()

        val expectedFields = route.fields.map { field ->
            NegotiatedPeerField.issue(
                field.id,
                field.kind,
                field.nullable,
                field.optional
            )
        }
        val expected = ManagedResultSchemaExpectation.create(
            endpointToken = endpointToken,
            profileId = route.profileId,
            abiMajor = route.abiMajor,
            schemaVersion = route.schemaVersion,
            contractMajor = route.contractMajor,
            contractMinor = route.contractMinor,
            featureBits = route.featureBits,
            requiredFeatureBits = route.requiredFeatureBits,
            serviceId = route.serviceId,
            methodId = route.methodId,
            resultDtoId = route.resultDtoId,
            resultDtoVersion = route.resultDtoVersion,
            localSchemaHashHex = managedManifestIdentity.schemaHashHex,
            peerSchemaHashHex = peer.schemaHashHex,
            fields = expectedFields,
            endpointLease = lease
        )
        return ManagedResultSchemaContext.issue(snapshot, expected)
    }

    private fun checkedRouteId(value: Long): Int {
        require(value in 1..Int.MAX_VALUE.toLong()) {
            "Managed result route ID cannot be represented by the Android JNI ABI."
        }
        return value.toInt()
    }

    public companion object {
        @JvmStatic
        public fun open(context: Context): NetLiftV5Endpoint =
            synchronized(processGate) {
                val applicationContext = context.applicationContext
                val config = readConfig(applicationContext)
                val runtimeDirectory = File(
                    applicationContext.filesDir,
                    "netlift/runtime/${RuntimeVariant.runtimeVersion}/dotnet"
                )
                val managedDirectory = File(
                    applicationContext.filesDir,
                    config.managedAssetDirectory
                )
                val processIdentity = listOf(
                    runtimeDirectory.absolutePath,
                    applicationContext.applicationInfo.nativeLibraryDir,
                    managedDirectory.absolutePath,
                    config.managedAssemblyName,
                    config.managedApiNamespace,
                    config.expectedManagedSchemaHashHex
                ).joinToString("|")

                val previousIdentity = initializedProcessIdentity
                val manifestIdentity: ManagedV5ManifestIdentity
                if (previousIdentity == null) {
                    copyAssetTree(
                        applicationContext,
                        "netlift/runtime/${RuntimeVariant.runtimeVersion}/dotnet",
                        runtimeDirectory
                    )
                    copyAssetTree(
                        applicationContext,
                        config.managedAssetDirectory,
                        managedDirectory
                    )
                    val initializeResult = NativeV5Api.nativeInitialize(
                        runtimeDirectory.path.toByteArray(StandardCharsets.UTF_8),
                        applicationContext.applicationInfo.nativeLibraryDir
                            .toByteArray(StandardCharsets.UTF_8),
                        managedDirectory.path.toByteArray(StandardCharsets.UTF_8),
                        config.managedAssemblyName,
                        config.managedApiNamespace
                    )
                    check(initializeResult == 0) {
                        "Generic v5 CoreCLR initialization failed: $initializeResult"
                    }
                    val managedManifest = NativeV5Api.nativeGetManifest()
                        ?: throw IllegalStateException("Managed v5 host returned no manifest.")
                    manifestIdentity = parseManagedV5Manifest(managedManifest.toStringUtf8())
                    require(manifestIdentity.schemaHashHex == config.expectedManagedSchemaHashHex) {
                        "Managed GetManifest hash does not match the packaged app pin."
                    }
                    initializedManagedManifestIdentity = manifestIdentity
                    initializedProcessIdentity = processIdentity
                } else {
                    check(previousIdentity == processIdentity) {
                        "A different v5 managed assembly is already initialized in this process."
                    }
                    manifestIdentity = initializedManagedManifestIdentity
                        ?: throw IllegalStateException(
                            "Managed v5 manifest identity was not retained after initialization."
                        )
                }

                val token = NativeV5Api.nativeCreateEndpoint(
                    if (config.localOptIn) 1 else 0,
                    config.minimumPeerMajor,
                    config.minimumPeerMinor
                )
                check(token > 0) { "Managed v5 host returned an invalid endpoint token." }
                NetLiftV5Endpoint(
                    token,
                    config.expectedManagedSchemaHashHex,
                    manifestIdentity,
                    config.defaultStartRequest,
                    PeerContextLease.create()
                )
            }

        private val processGate = Any()
        private var initializedProcessIdentity: String? = null
        private var initializedManagedManifestIdentity: ManagedV5ManifestIdentity? = null

        private fun readConfig(context: Context): PackagedHostConfig {
            val path = "netlift/apps/${context.packageName}/v5-host.json"
            val json = context.assets.open(path).bufferedReader(StandardCharsets.UTF_8).use {
                it.readText()
            }
            val value = JSONObject(json)
            val expectedManagedDirectory =
                "netlift/apps/${context.packageName}/dotnet"
            val managedAssetDirectory = value.getString("managedAssetDirectory")
            require(managedAssetDirectory == expectedManagedDirectory) {
                "Managed v5 asset directory must be scoped to this app package."
            }
            val assemblyName = value.getString("managedAssemblyName")
            require(assemblyName.matches(Regex("[A-Za-z0-9_.]+"))) {
                "Managed v5 assembly name contains unsupported characters."
            }
            val apiNamespace = if (value.has("managedApiNamespace")) {
                value.getString("managedApiNamespace")
            } else {
                assemblyName
            }
            require(apiNamespace.matches(Regex("[A-Za-z0-9_.]+"))) {
                "Managed v5 API namespace contains unsupported characters."
            }
            val managedHash =
                value.getString("expectedManagedSchemaHashHex").lowercase()
            require(managedHash.isSha256Hex()) {
                "Packaged managed schema hash must be a full SHA-256 value."
            }
            val localMinimumMajor = value.getInt("minimumPeerMajor")
            val localMinimumMinor = value.getInt("minimumPeerMinor")
            require(localMinimumMajor > 0 && localMinimumMinor >= 0) {
                "Packaged local compatible-start policy is invalid."
            }
            val request = CompatibleStartRequest(
                value.getString("peerManifestJson"),
                value.getString("trustedPeerHashHex"),
                value.getInt("trustedMinimumMajor"),
                value.getInt("trustedMinimumMinor"),
                value.getBoolean("peerOptIn")
            )
            return PackagedHostConfig(
                managedAssetDirectory,
                assemblyName,
                apiNamespace,
                managedHash,
                value.getBoolean("localOptIn"),
                localMinimumMajor,
                localMinimumMinor,
                request
            )
        }

        private fun copyAssetTree(context: Context, assetPath: String, target: File) {
            val children = context.assets.list(assetPath)
                ?: throw IllegalStateException("Missing packaged assets at $assetPath.")
            check(children.isNotEmpty()) { "Packaged asset directory is empty: $assetPath." }
            target.mkdirs()
            for (child in children) {
                require(child != "." && child != ".." && !child.contains('/')) {
                    "Invalid packaged asset path."
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
    }
}

internal fun parseV5PeerManifest(manifestJson: String): V5PeerSchemaIdentity {
    require(manifestJson.length in 1..MAX_MANIFEST_BYTES) {
        "Peer manifest length is outside supported bounds."
    }
    require(manifestJson.toByteArray(StandardCharsets.UTF_8).size <= MAX_MANIFEST_BYTES) {
        "Peer manifest UTF-8 length is outside supported bounds."
    }
    val root = JSONObject(manifestJson)
    val schema = root.getJSONObject("schema")
    val hash = root.getString("schemaHash").lowercase()
    require(hash.isSha256Hex()) { "Peer manifest schemaHash is invalid." }
    val contractRevision = schema.getJSONObject("contractRevision")
    val service = schema.getJSONObject("service")
    val method = schema.getJSONObject("method")
    val dto = if (schema.optString("profile") == ASYNC_CALLBACK_PROFILE_NAME) {
        findDto(schema, method.getLong("requestDtoId"))
    } else {
        schema.getJSONObject("dto")
    }
    val rawFields = dto.getJSONArray("fields")
    require(rawFields.length() in 1..MAX_FIELDS) {
        "Peer manifest field count is outside supported bounds."
    }

    val fields = ArrayList<V5PeerFieldDescriptor>(rawFields.length())
    var previousId = 0
    repeat(rawFields.length()) { index ->
        val field = rawFields.getJSONObject(index)
        val idLong = field.getLong("id")
        require(idLong in 1..0xffffL && idLong > previousId) {
            "Peer manifest field IDs must be positive, unique, and ascending."
        }
        val kind = when (field.getString("type")) {
            "utf8" -> NegotiatedPeerFieldKind.UTF8
            "i32" -> NegotiatedPeerFieldKind.I32
            "bool" -> NegotiatedPeerFieldKind.BOOL
            else -> throw IllegalArgumentException("Peer manifest field kind is unsupported.")
        }
        val nullable = field.getBoolean("nullable")
        val optional = field.getBoolean("optional")
        require(!nullable || kind == NegotiatedPeerFieldKind.UTF8) {
            "Only UTF8 peer fields may be nullable."
        }
        fields.add(
            V5PeerFieldDescriptor(idLong.toInt(), kind, nullable, optional)
        )
        previousId = idLong.toInt()
    }

    val identity = V5PeerSchemaIdentity(
        hash,
        schema.getLong("abiMajor"),
        schema.getLong("schemaVersion"),
        contractRevision.getLong("major"),
        contractRevision.getLong("minor"),
        schema.getLong("featureBits"),
        schema.getLong("requiredFeatureBits"),
        service.getLong("id"),
        method.getLong("id"),
        dto.getLong("id"),
        dto.getLong("version"),
        fields
    )
    require(identity.abiMajor in 1..UINT32_MAX &&
        identity.schemaVersion in 1..UINT32_MAX &&
        identity.contractMajor in 1..UINT32_MAX &&
        identity.contractMinor in 0..UINT32_MAX &&
        identity.featureBits in 0..UINT32_MAX &&
        identity.requiredFeatureBits in 0..UINT32_MAX &&
        (identity.requiredFeatureBits and identity.featureBits.inv()) == 0L &&
        identity.serviceId in 1..UINT32_MAX &&
        identity.methodId in 1..UINT32_MAX &&
        identity.dtoId in 1..UINT32_MAX &&
        identity.dtoVersion in 1..UINT32_MAX
    ) { "Peer manifest fixed metadata is outside supported bounds." }
    return identity
}

private fun encodePeerHello(peer: V5PeerSchemaIdentity): ByteArray =
    ByteBuffer.allocate(PEER_HELLO_SIZE)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put(byteArrayOf(0x4e, 0x4c, 0x43, 0x53))
        .putShort(1)
        .putInt(peer.abiMajor.toInt())
        .put(peer.schemaHashHex.hexToBytes())
        .putInt(peer.contractMajor.toInt())
        .putInt(peer.contractMinor.toInt())
        .putInt(peer.featureBits.toInt())
        .array()

private fun ByteArray.toStringUtf8(): String =
    StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(this)).toString()

private data class PackagedHostConfig(
    val managedAssetDirectory: String,
    val managedAssemblyName: String,
    val managedApiNamespace: String,
    val expectedManagedSchemaHashHex: String,
    val localOptIn: Boolean,
    val minimumPeerMajor: Int,
    val minimumPeerMinor: Int,
    val defaultStartRequest: CompatibleStartRequest
)

internal object NativeV5Api {
    init {
        // ART must run this JNI library's JNI_OnLoad before CoreCLR loads it through P/Invoke.
        System.loadLibrary("System.Security.Cryptography.Native.Android")
        System.loadLibrary("netlift_androidruntime")
    }

    @JvmSynthetic
    external fun nativeInitialize(
        runtimeDirectoryUtf8: ByteArray,
        nativeLibraryDirectoryUtf8: ByteArray,
        managedDirectoryUtf8: ByteArray,
        managedAssemblyName: String,
        managedApiNamespace: String
    ): Int

    @JvmSynthetic
    external fun nativeCreateEndpoint(
        localOptIn: Int,
        minimumPeerMajor: Int,
        minimumPeerMinor: Int
    ): Long

    @JvmSynthetic
    external fun nativeGetManifest(): ByteArray?

    @JvmSynthetic
    external fun nativeStart(
        endpoint: Long,
        peerManifest: ByteArray,
        peerHello: ByteArray,
        trustedPeerHash: ByteArray,
        trustedMinimumMajor: Int,
        trustedMinimumMinor: Int,
        peerOptIn: Boolean
    )

    @JvmSynthetic
    external fun nativeGetPeerSchema(endpoint: Long): ByteArray?

    @JvmSynthetic
    external fun nativeGetManagedResultSchema(
        endpoint: Long,
        serviceId: Int,
        methodId: Int,
        destination: ByteArray
    ): LongArray?

    @JvmSynthetic
    external fun nativeDispatch(endpoint: Long, payload: ByteArray): Int

    @JvmSynthetic
    external fun nativeGetCounters(): LongArray?

    @JvmSynthetic
    external fun nativeCloseEndpoint(endpoint: Long)

    @JvmSynthetic
    external fun nativeBindAsyncV2(
        endpoint: Long,
        peerSchema: ByteArray,
        resultSchema: ByteArray,
        expectedManagedHash: ByteArray,
        callbacks: com.netlift.androidruntime.NetLiftNativeCallbacksV2
    ): LongArray?

    @JvmSynthetic
    external fun nativeBeginAsyncV2(
        endpoint: Long,
        registrationToken: Long,
        registrationGeneration: Long,
        serviceId: Int,
        methodId: Int,
        payload: ByteArray
    ): Long

    @JvmSynthetic
    external fun nativeActivateAsyncV2(
        endpoint: Long,
        registrationToken: Long,
        registrationGeneration: Long,
        operationToken: Long
    )

    @JvmSynthetic
    external fun nativeCancelAsyncV2(
        endpoint: Long,
        registrationToken: Long,
        registrationGeneration: Long,
        operationToken: Long
    )

    @JvmSynthetic
    external fun nativeCancelRequestAsyncV2(
        endpoint: Long,
        registrationToken: Long,
        registrationGeneration: Long,
        requestToken: Long
    )

    @JvmSynthetic
    external fun nativeReplyTypedAsyncV2(
        endpoint: Long,
        registrationToken: Long,
        registrationGeneration: Long,
        requestToken: Long,
        outcome: Int,
        accepted: Boolean,
        diagnostic: ByteArray
    )

    @JvmSynthetic
    external fun nativeRevokeAsyncV2(
        endpoint: Long,
        registrationToken: Long,
        registrationGeneration: Long
    )

    @JvmSynthetic
    external fun nativeAckDrainAsyncV2(
        endpoint: Long,
        registrationToken: Long,
        registrationGeneration: Long
    )

    @JvmSynthetic
    external fun nativeUnbindAsyncV2(endpoint: Long)

    @JvmSynthetic
    external fun nativeAsyncV2LeaseSnapshot(): LongArray?
}

private const val MAX_DTO_BYTES = 64 * 1024
private const val MAX_FIELDS = 64
private const val PEER_HELLO_SIZE = 54
private const val UINT32_MAX = 0xffff_ffffL
private const val START_ALREADY_ATTEMPTED = 6
private const val COUNTER_COUNT = 4
private const val MANAGED_RESULT_SCHEMA_HEADER_SIZE = 136
private const val MANAGED_RESULT_SCHEMA_FIELD_SIZE = 4
private const val MANAGED_RESULT_SCHEMA_MAX_SIZE = 392
private const val RESULT_SCHEMA_COUNTS = 3
private const val SHORT_BUFFER_SENTINEL = 0x5a.toByte()
private const val SCHEMA_BUFFER_TOO_SMALL = 12
private const val OK = 0
private const val ASYNC_CALLBACK_PROFILE_NAME = "async-callback-v1"
