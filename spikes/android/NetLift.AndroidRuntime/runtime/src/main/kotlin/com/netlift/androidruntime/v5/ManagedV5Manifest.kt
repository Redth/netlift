package com.netlift.androidruntime.v5

import org.json.JSONObject

internal data class ManagedResultRouteIdentity(
    val profileId: Long,
    val abiMajor: Long,
    val schemaVersion: Long,
    val contractMajor: Long,
    val contractMinor: Long,
    val featureBits: Long,
    val requiredFeatureBits: Long,
    val serviceId: Long,
    val methodId: Long,
    val resultDtoId: Long,
    val resultDtoVersion: Long,
    val fields: List<V5PeerFieldDescriptor>
)

internal data class ManagedV5ManifestIdentity(
    val schemaHashHex: String,
    val resultRoute: ManagedResultRouteIdentity?
)

internal fun parseManagedV5Manifest(manifestJson: String): ManagedV5ManifestIdentity {
    require(manifestJson.length in 1..MAX_MANIFEST_BYTES) {
        "Managed manifest length is outside supported bounds."
    }
    require(manifestJson.toByteArray(Charsets.UTF_8).size <= MAX_MANIFEST_BYTES) {
        "Managed manifest UTF-8 length is outside supported bounds."
    }
    val root = JSONObject(manifestJson)
    val hash = root.getString("schemaHash").lowercase()
    require(hash.isSha256Hex()) { "Managed manifest schemaHash is invalid." }
    val schema = root.getJSONObject("schema")
    val resultRoute = if (schema.optString("profile") == ASYNC_CALLBACK_PROFILE_NAME) {
        parseManagedResultRoute(schema)
    } else {
        null
    }
    return ManagedV5ManifestIdentity(hash, resultRoute)
}

private fun parseManagedResultRoute(schema: JSONObject): ManagedResultRouteIdentity {
    val contractRevision = schema.getJSONObject("contractRevision")
    val service = schema.getJSONObject("service")
    val method = schema.getJSONObject("method")
    val resultDtoId = method.getLong("resultDtoId")
    val resultDto = findDto(schema, resultDtoId)
    val rawFields = resultDto.getJSONArray("fields")
    require(rawFields.length() in 1..MAX_FIELDS) {
        "Managed result field count is outside supported bounds."
    }

    val fields = ArrayList<V5PeerFieldDescriptor>(rawFields.length())
    var previousId = 0
    repeat(rawFields.length()) { index ->
        val field = rawFields.getJSONObject(index)
        val idLong = field.getLong("id")
        require(idLong in 1..0xffffL && idLong > previousId) {
            "Managed result field IDs must be positive, unique, and ascending."
        }
        val kind = when (field.getString("type")) {
            "utf8" -> NegotiatedPeerFieldKind.UTF8
            "i32" -> NegotiatedPeerFieldKind.I32
            "bool" -> NegotiatedPeerFieldKind.BOOL
            else -> throw IllegalArgumentException("Managed result field kind is unsupported.")
        }
        val nullable = field.getBoolean("nullable")
        require(!nullable || kind == NegotiatedPeerFieldKind.UTF8) {
            "Only UTF8 managed result fields may be nullable."
        }
        fields.add(
            V5PeerFieldDescriptor(
                idLong.toInt(),
                kind,
                nullable,
                field.getBoolean("optional")
            )
        )
        previousId = idLong.toInt()
    }

    val result = ManagedResultRouteIdentity(
        profileId = ASYNC_CALLBACK_PROFILE_ID,
        abiMajor = schema.getLong("abiMajor"),
        schemaVersion = schema.getLong("schemaVersion"),
        contractMajor = contractRevision.getLong("major"),
        contractMinor = contractRevision.getLong("minor"),
        featureBits = schema.getLong("featureBits"),
        requiredFeatureBits = schema.getLong("requiredFeatureBits"),
        serviceId = service.getLong("id"),
        methodId = method.getLong("id"),
        resultDtoId = resultDtoId,
        resultDtoVersion = resultDto.getLong("version"),
        fields = fields
    )
    require(result.abiMajor in 1..UINT32_MAX &&
        result.schemaVersion in 1..UINT32_MAX &&
        result.contractMajor in 1..UINT32_MAX &&
        result.contractMinor in 0..UINT32_MAX &&
        result.featureBits in 0..UINT32_MAX &&
        result.requiredFeatureBits in 0..UINT32_MAX &&
        (result.requiredFeatureBits and result.featureBits.inv()) == 0L &&
        (result.featureBits and REQUIRED_ASYNC_FEATURES) == REQUIRED_ASYNC_FEATURES &&
        (result.requiredFeatureBits and REQUIRED_ASYNC_FEATURES) == REQUIRED_ASYNC_FEATURES &&
        result.serviceId in 1..UINT32_MAX &&
        result.methodId in 1..UINT32_MAX &&
        result.resultDtoId in 1..UINT32_MAX &&
        result.resultDtoVersion in 1..UINT32_MAX
    ) { "Managed result route metadata is outside the supported async profile." }
    return result
}

internal fun findDto(schema: JSONObject, dtoId: Long): JSONObject {
    val dtos = schema.getJSONArray("dtos")
    for (index in 0 until dtos.length()) {
        val dto = dtos.getJSONObject(index)
        if (dto.getLong("id") == dtoId)
            return dto
    }
    throw IllegalArgumentException("Manifest does not contain its declared DTO $dtoId.")
}

internal const val MAX_MANIFEST_BYTES = 16 * 1024
private const val MAX_FIELDS = 64
private const val UINT32_MAX = 0xffff_ffffL
private const val ASYNC_CALLBACK_PROFILE_ID = 1L
private const val ASYNC_CALLBACK_PROFILE_NAME = "async-callback-v1"
private const val REQUIRED_ASYNC_FEATURES = 0x36L
