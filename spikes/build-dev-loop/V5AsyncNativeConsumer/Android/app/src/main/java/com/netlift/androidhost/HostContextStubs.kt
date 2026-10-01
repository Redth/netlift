package com.netlift.androidruntime.v5

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

public class NegotiatedPeerContext internal constructor(
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
    public val localSchemaHashHex: String,
    public val schemaHashHex: String,
    public val fields: List<NegotiatedPeerField>
) {
    public fun requireActive() {}
}

public class ManagedResultSchemaContext internal constructor(
    public val profileId: Long,
    public val abiMajor: Long,
    public val schemaVersion: Long,
    public val contractMajor: Long,
    public val contractMinor: Long,
    public val featureBits: Long,
    public val requiredFeatureBits: Long,
    public val serviceId: Long,
    public val methodId: Long,
    public val resultDtoId: Long,
    public val resultDtoVersion: Long,
    public val localSchemaHashHex: String,
    public val peerSchemaHashHex: String,
    public val fields: List<NegotiatedPeerField>
) {
    public fun requireActive() {}
}
