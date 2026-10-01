package com.netlift.androidruntime.v5

import java.io.File

public enum class NegotiatedPeerFieldKind(public val wireCode: Int) {
    UTF8(1),
    I32(2),
    BOOL(3)
}

public data class NegotiatedPeerField(
    public val id: Int,
    public val kind: NegotiatedPeerFieldKind,
    public val nullable: Boolean,
    public val optional: Boolean
)

public enum class PeerContextTestMutation {
    NONE,
    INACTIVE,
    INVALID_LOCAL_HASH,
    WRONG_ABI,
    WRONG_SCHEMA_VERSION,
    WRONG_CONTRACT_MAJOR,
    WRONG_MINOR,
    UNSUPPORTED_FEATURE,
    UNSUPPORTED_REQUIRED_FEATURE,
    WRONG_SERVICE,
    WRONG_METHOD,
    WRONG_DTO,
    WRONG_DTO_VERSION,
    WRONG_PEER_HASH,
    INVALID_PEER_HASH,
    UNKNOWN_REQUIRED_FIELD,
    INCOMPATIBLE_KIND,
    INCOMPATIBLE_NULLABILITY,
    INCOMPATIBLE_OPTIONALITY,
    DUPLICATE_FIELD
}

public class NegotiatedPeerContext private constructor(
    public val localSchemaHashHex: String,
    public val schemaHashHex: String,
    public val abiMajor: Long,
    public val schemaVersion: Long,
    public val contractMajor: Long,
    public val contractMinor: Long,
    public val featureBits: Long,
    public val requiredFeatureBits: Long,
    public val serviceId: Long,
    public val methodId: Long,
    public val dtoId: Long,
    public val dtoVersion: Long,
    public val fields: List<NegotiatedPeerField>,
    private var active: Boolean
) {
    public fun requireActive() {
        check(active) { "Negotiated test peer context is inactive." }
    }

    public fun revokeForTesting() {
        active = false
    }

    public companion object {
        public fun testingOnly(path: String, mutation: PeerContextTestMutation = PeerContextTestMutation.NONE): NegotiatedPeerContext {
            val values = linkedMapOf<String, String>()
            val fields = mutableListOf<NegotiatedPeerField>()
            File(path).readLines().filter { it.isNotEmpty() }.forEach { line ->
                val separator = line.indexOf('=')
                require(separator > 0) { "Malformed context fixture line." }
                val key = line.substring(0, separator)
                val value = line.substring(separator + 1)
                if (key == "field") {
                    val parts = value.split('|')
                    require(parts.size == 4) { "Malformed context field fixture." }
                    val kind = when (parts[1]) {
                        "utf8" -> NegotiatedPeerFieldKind.UTF8
                        "i32" -> NegotiatedPeerFieldKind.I32
                        "bool" -> NegotiatedPeerFieldKind.BOOL
                        else -> error("Unknown peer field kind.")
                    }
                    fields.add(NegotiatedPeerField(
                        id = parts[0].toInt(),
                        kind = kind,
                        nullable = parts[2] == "1",
                        optional = parts[3] == "1"
                    ))
                } else {
                    values[key] = value
                }
            }
            fun number(key: String): Long = values.getValue(key).toLong()
            var localHash = values.getValue("localSchemaHash")
            var peerHash = values.getValue("schemaHash")
            var abiMajor = number("abiMajor")
            var schemaVersion = number("schemaVersion")
            var contractMajor = number("contractMajor")
            var contractMinor = number("contractMinor")
            var featureBits = number("featureBits")
            var requiredFeatureBits = number("requiredFeatureBits")
            var serviceId = number("serviceID")
            var methodId = number("methodID")
            var dtoId = number("dtoID")
            var dtoVersion = number("dtoVersion")
            var active = true
            when (mutation) {
                PeerContextTestMutation.NONE -> Unit
                PeerContextTestMutation.INACTIVE -> active = false
                PeerContextTestMutation.INVALID_LOCAL_HASH -> localHash = "not-a-hash"
                PeerContextTestMutation.WRONG_ABI -> abiMajor = 2
                PeerContextTestMutation.WRONG_SCHEMA_VERSION -> schemaVersion = 4
                PeerContextTestMutation.WRONG_CONTRACT_MAJOR -> contractMajor++
                PeerContextTestMutation.WRONG_MINOR -> contractMinor = if (contractMinor == 0L) 1 else 0
                PeerContextTestMutation.UNSUPPORTED_FEATURE -> featureBits = featureBits or 0x20
                PeerContextTestMutation.UNSUPPORTED_REQUIRED_FEATURE -> requiredFeatureBits = requiredFeatureBits or 0x1
                PeerContextTestMutation.WRONG_SERVICE -> serviceId++
                PeerContextTestMutation.WRONG_METHOD -> methodId++
                PeerContextTestMutation.WRONG_DTO -> dtoId++
                PeerContextTestMutation.WRONG_DTO_VERSION -> dtoVersion++
                PeerContextTestMutation.WRONG_PEER_HASH -> peerHash = "0".repeat(64)
                PeerContextTestMutation.INVALID_PEER_HASH -> peerHash = "not-a-hash"
                PeerContextTestMutation.UNKNOWN_REQUIRED_FIELD ->
                    fields.add(NegotiatedPeerField(99, NegotiatedPeerFieldKind.UTF8, false, false))
                PeerContextTestMutation.INCOMPATIBLE_KIND -> {
                    val index = fields.indexOfFirst { it.id == 1 }
                    require(index >= 0)
                    fields[index] = NegotiatedPeerField(1, NegotiatedPeerFieldKind.BOOL, false, false)
                }
                PeerContextTestMutation.INCOMPATIBLE_NULLABILITY -> {
                    val index = fields.indexOfFirst { it.id == 1 }
                    require(index >= 0)
                    fields[index] = NegotiatedPeerField(1, NegotiatedPeerFieldKind.UTF8, true, false)
                }
                PeerContextTestMutation.INCOMPATIBLE_OPTIONALITY -> {
                    val index = fields.indexOfFirst { it.id == 1 }
                    require(index >= 0)
                    fields[index] = NegotiatedPeerField(1, NegotiatedPeerFieldKind.UTF8, false, true)
                }
                PeerContextTestMutation.DUPLICATE_FIELD -> {
                    require(fields.isNotEmpty())
                    fields.add(fields.first())
                }
            }
            return NegotiatedPeerContext(
                localSchemaHashHex = localHash,
                schemaHashHex = peerHash,
                abiMajor = abiMajor,
                schemaVersion = schemaVersion,
                contractMajor = contractMajor,
                contractMinor = contractMinor,
                featureBits = featureBits,
                requiredFeatureBits = requiredFeatureBits,
                serviceId = serviceId,
                methodId = methodId,
                dtoId = dtoId,
                dtoVersion = dtoVersion,
                fields = fields.toList(),
                active = active
            )
        }
    }
}
