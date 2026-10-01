package com.netlift.androidruntime

import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets

public data class NetLiftRuntimeInfo(
    val abiVersion: Int,
    val featureBits: Int,
    val schemaHash: ByteArray
)

public interface NetLiftNativeCallbacks {
    public fun onRequest(
        registrationToken: Long,
        requestToken: Long,
        methodId: Int,
        payload: ByteArray
    )

    public fun onResult(
        operationToken: Long,
        status: Int,
        result: ByteArray,
        error: ByteArray
    )

    public fun onRelease(registrationToken: Long)
}

public data class NetLiftNativeRegistrationV2(
    val token: Long,
    val generation: Long
)

public interface NetLiftNativeCallbacksV2 : NetLiftNativeCallbacks {
    public fun onRevoke(registrationToken: Long, generation: Long)
}

public interface NetLiftStreamCallbacks {
    public fun onNext(token: Long, borrowedItem: ByteArray)
    public fun onTerminal(token: Long, status: Int, diagnostic: ByteArray)
    public fun onRelease(token: Long)
}

public data class NetLiftTypedReply(
    val outcome: Int,
    val accepted: Boolean = false,
    val diagnostic: ByteArray = byteArrayOf()
)

public class NativeAbiException(
    public val statusCode: Int,
    diagnosticUtf8: ByteArray
) : IllegalStateException(decodeDiagnostic(diagnosticUtf8)) {
    private companion object {
        fun decodeDiagnostic(value: ByteArray): String =
            StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(value))
                .toString()
    }
}
