package com.netlift.androidruntime.v5

import java.io.File
import java.util.Collections

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

public enum class AsyncHostContextTestMutation {
    NONE,
    INACTIVE,
    INVALID_LOCAL_HASH,
    WRONG_PEER_HASH,
    WRONG_PROFILE,
    WRONG_ABI,
    WRONG_FEATURES,
    WRONG_CONTRACT,
    WRONG_SERVICE,
    WRONG_METHOD,
    WRONG_DTO,
    WRONG_DTO_VERSION,
    UNKNOWN_REQUIRED_FIELD,
    INCOMPATIBLE_KIND,
    DUPLICATE_FIELD,
    MISSING_REQUIRED_FIELD
}

private data class ContextFixture(
    val values: MutableMap<String, String>,
    val fields: MutableList<NegotiatedPeerField>,
    var active: Boolean = true
)

private fun readFixture(path: String, mutation: AsyncHostContextTestMutation): ContextFixture {
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
                else -> error("Unknown fixture field kind.")
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
    val fixture = ContextFixture(values, fields)
    when (mutation) {
        AsyncHostContextTestMutation.NONE -> Unit
        AsyncHostContextTestMutation.INACTIVE -> fixture.active = false
        AsyncHostContextTestMutation.INVALID_LOCAL_HASH -> fixture.values["localSchemaHash"] = "invalid"
        AsyncHostContextTestMutation.WRONG_PEER_HASH -> {
            fixture.values["schemaHash"] = "0".repeat(64)
            fixture.values["peerSchemaHash"] = "0".repeat(64)
        }
        AsyncHostContextTestMutation.WRONG_PROFILE -> fixture.values["profileId"] = "2"
        AsyncHostContextTestMutation.WRONG_ABI -> fixture.values["abiMajor"] = "2"
        AsyncHostContextTestMutation.WRONG_FEATURES -> {
            fixture.values["featureBits"] = "16"
            fixture.values["requiredFeatureBits"] = "16"
        }
        AsyncHostContextTestMutation.WRONG_CONTRACT -> fixture.values["contractMinor"] = "999"
        AsyncHostContextTestMutation.WRONG_SERVICE -> fixture.values["serviceID"] = "99"
        AsyncHostContextTestMutation.WRONG_METHOD -> fixture.values["methodID"] = "99"
        AsyncHostContextTestMutation.WRONG_DTO -> {
            fixture.values["dtoID"] = "99"
            fixture.values["resultDTOID"] = "99"
        }
        AsyncHostContextTestMutation.WRONG_DTO_VERSION -> {
            fixture.values["dtoVersion"] = "2"
            fixture.values["resultDTOVersion"] = "2"
        }
        AsyncHostContextTestMutation.UNKNOWN_REQUIRED_FIELD ->
            fixture.fields.add(NegotiatedPeerField(99, NegotiatedPeerFieldKind.UTF8, false, false))
        AsyncHostContextTestMutation.INCOMPATIBLE_KIND -> {
            val index = fixture.fields.indexOfFirst { it.id == 1 }
            require(index >= 0)
            fixture.fields[index] = NegotiatedPeerField(1, NegotiatedPeerFieldKind.BOOL, false, false)
        }
        AsyncHostContextTestMutation.DUPLICATE_FIELD -> {
            require(fixture.fields.isNotEmpty())
            fixture.fields.add(fixture.fields.first())
        }
        AsyncHostContextTestMutation.MISSING_REQUIRED_FIELD ->
            fixture.fields.removeAll { it.id == 1 }
    }
    return fixture
}

private fun ContextFixture.number(key: String): Long = values.getValue(key).toLong()
private fun ContextFixture.text(key: String): String = values.getValue(key)

public class NegotiatedPeerContext private constructor(fixture: ContextFixture) {
    public val localSchemaHashHex: String = fixture.text("localSchemaHash")
    public val schemaHashHex: String = fixture.text("schemaHash")
    public val abiMajor: Long = fixture.number("abiMajor")
    public val schemaVersion: Long = fixture.number("schemaVersion")
    public val contractMajor: Long = fixture.number("contractMajor")
    public val contractMinor: Long = fixture.number("contractMinor")
    public val featureBits: Long = fixture.number("featureBits")
    public val requiredFeatureBits: Long = fixture.number("requiredFeatureBits")
    public val serviceId: Long = fixture.number("serviceID")
    public val methodId: Long = fixture.number("methodID")
    public val dtoId: Long = fixture.number("dtoID")
    public val dtoVersion: Long = fixture.number("dtoVersion")
    public val fields: List<NegotiatedPeerField> =
        Collections.unmodifiableList(ArrayList(fixture.fields))
    private var active: Boolean = fixture.active

    public fun requireActive() {
        check(active) { "Negotiated test peer context is inactive." }
    }

    public fun revokeForTesting() {
        active = false
    }

    public companion object {
        public fun testingOnly(
            path: String,
            mutation: AsyncHostContextTestMutation = AsyncHostContextTestMutation.NONE
        ): NegotiatedPeerContext = NegotiatedPeerContext(readFixture(path, mutation))
    }
}

public class ManagedResultSchemaContext private constructor(fixture: ContextFixture) {
    public val localSchemaHashHex: String = fixture.text("localSchemaHash")
    public val peerSchemaHashHex: String = fixture.text("peerSchemaHash")
    public val abiMajor: Long = fixture.number("abiMajor")
    public val schemaVersion: Long = fixture.number("schemaVersion")
    public val contractMajor: Long = fixture.number("contractMajor")
    public val contractMinor: Long = fixture.number("contractMinor")
    public val featureBits: Long = fixture.number("featureBits")
    public val requiredFeatureBits: Long = fixture.number("requiredFeatureBits")
    public val profileId: Long = fixture.number("profileId")
    public val serviceId: Long = fixture.number("serviceID")
    public val methodId: Long = fixture.number("methodID")
    public val resultDtoId: Long = fixture.number("resultDTOID")
    public val resultDtoVersion: Long = fixture.number("resultDTOVersion")
    public val fields: List<NegotiatedPeerField> =
        Collections.unmodifiableList(ArrayList(fixture.fields))
    private var active: Boolean = fixture.active

    public fun requireActive() {
        check(active) { "Managed-result test context is inactive." }
    }

    public fun revokeForTesting() {
        active = false
    }

    public companion object {
        public fun testingOnly(
            path: String,
            mutation: AsyncHostContextTestMutation = AsyncHostContextTestMutation.NONE
        ): ManagedResultSchemaContext = ManagedResultSchemaContext(readFixture(path, mutation))
    }
}
