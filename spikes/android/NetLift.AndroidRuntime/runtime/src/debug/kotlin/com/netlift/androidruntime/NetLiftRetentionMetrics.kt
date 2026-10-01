package com.netlift.androidruntime

import android.os.Debug

public data class NetLiftRetentionMemorySample(
    val managedSnapshot: LongArray,
    val nativeSnapshot: LongArray,
    val nativeHeapAllocatedBytes: Long,
    val nativeHeapSizeBytes: Long,
    val totalPssKb: Long,
    val dalvikPssKb: Long,
    val nativePssKb: Long,
    val otherPssKb: Long
)

public data class NetLiftRetentionMetricAssessment(
    val name: String,
    val status: String,
    val baselineMedian: Long,
    val baselineRange: Long,
    val maximumMeasuredMedian: Long,
    val slopePerBatch: Double,
    val growthLimit: Long,
    val slopeLimit: Long
)

public object NetLiftRetentionMetrics {
    public const val SamplesPerCheckpoint: Int = 5
    public const val ManagedGrowthLimitBytes: Long = 2L * 1024 * 1024
    public const val ManagedSlopeLimitBytes: Long = 1L * 1024 * 1024
    public const val NativeGrowthLimitBytes: Long = 4L * 1024 * 1024
    public const val NativeSlopeLimitBytes: Long = 2L * 1024 * 1024
    public const val PssInvestigationAlertKb: Long = 16L * 1024

    public fun sample(): NetLiftRetentionMemorySample {
        val managed = requireNotNull(NetLiftNativeFailureHooks.managedRetentionSnapshot()) {
            "Managed retention diagnostic snapshot is unavailable."
        }
        val native = requireNotNull(NetLiftNativeFailureHooks.ownershipSnapshot()) {
            "Native ownership snapshot is unavailable."
        }
        val memoryInfo = Debug.MemoryInfo()
        Debug.getMemoryInfo(memoryInfo)
        return NetLiftRetentionMemorySample(
            managedSnapshot = managed,
            nativeSnapshot = native,
            nativeHeapAllocatedBytes = Debug.getNativeHeapAllocatedSize(),
            nativeHeapSizeBytes = Debug.getNativeHeapSize(),
            totalPssKb = memoryInfo.totalPss.toLong(),
            dalvikPssKb = memoryInfo.dalvikPss.toLong(),
            nativePssKb = memoryInfo.nativePss.toLong(),
            otherPssKb = memoryInfo.otherPss.toLong()
        )
    }

    public fun median(values: List<Long>): Long {
        require(values.isNotEmpty()) { "Cannot compute a median from no samples." }
        val sorted = values.sorted()
        return sorted[sorted.size / 2]
    }

    public fun range(values: List<Long>): Long {
        require(values.isNotEmpty()) { "Cannot compute a range from no samples." }
        return (values.maxOrNull() ?: error("No maximum sample.")) -
            (values.minOrNull() ?: error("No minimum sample."))
    }

    public fun assess(
        name: String,
        checkpoints: List<List<NetLiftRetentionMemorySample>>,
        value: (NetLiftRetentionMemorySample) -> Long,
        growthLimit: Long,
        slopeLimit: Long
    ): NetLiftRetentionMetricAssessment {
        require(checkpoints.size == 4) {
            "Expected one warm-up baseline and three measured checkpoints."
        }
        require(checkpoints.all { it.size == SamplesPerCheckpoint }) {
            "Every retention checkpoint must contain exactly five memory samples."
        }
        val medians = checkpoints.map { samples -> median(samples.map(value)) }
        val baselineRange = range(checkpoints.first().map(value))
        val maximumMeasuredMedian = medians.drop(1).maxOrNull()
            ?: error("Measured checkpoint medians are missing.")
        val slope = linearSlope(medians)
        val status = when {
            baselineRange > growthLimit -> "INCONCLUSIVE_BASELINE_NOISE"
            maximumMeasuredMedian - medians.first() > growthLimit -> "FAIL_GROWTH_LIMIT"
            slope > slopeLimit.toDouble() -> "FAIL_SLOPE_LIMIT"
            else -> "PASS"
        }
        return NetLiftRetentionMetricAssessment(
            name = name,
            status = status,
            baselineMedian = medians.first(),
            baselineRange = baselineRange,
            maximumMeasuredMedian = maximumMeasuredMedian,
            slopePerBatch = slope,
            growthLimit = growthLimit,
            slopeLimit = slopeLimit
        )
    }

    public fun formatCheckpoint(
        name: String,
        samples: List<NetLiftRetentionMemorySample>
    ): String = buildString {
        val managedLive = samples.map { it.managedSnapshot[27] }
        val managedHeap = samples.map { it.managedSnapshot[28] }
        val managedFragmented = samples.map { it.managedSnapshot[29] }
        val managedCommitted = samples.map { it.managedSnapshot[30] }
        val nativeAllocated = samples.map { it.nativeHeapAllocatedBytes }
        val nativeHeapSize = samples.map { it.nativeHeapSizeBytes }
        val totalPss = samples.map { it.totalPssKb }
        val dalvikPss = samples.map { it.dalvikPssKb }
        val nativePss = samples.map { it.nativePssKb }
        val otherPss = samples.map { it.otherPssKb }
        append(name)
        append(": medians/ranges ")
        append("managedLive=${median(managedLive)}/${range(managedLive)}B ")
        append("managedHeap=${median(managedHeap)}/${range(managedHeap)}B ")
        append("managedFragmented=${median(managedFragmented)}/${range(managedFragmented)}B ")
        append("managedCommitted=${median(managedCommitted)}/${range(managedCommitted)}B ")
        append("gcCollections=${samples.last().managedSnapshot[31]}," +
            "${samples.last().managedSnapshot[32]},${samples.last().managedSnapshot[33]} ")
        append("nativeAllocated=${median(nativeAllocated)}/${range(nativeAllocated)}B ")
        append("nativeHeapSize=${median(nativeHeapSize)}/${range(nativeHeapSize)}B ")
        append("pssTotal=${median(totalPss)}/${range(totalPss)}KB ")
        append("pssDalvik=${median(dalvikPss)}/${range(dalvikPss)}KB ")
        append("pssNative=${median(nativePss)}/${range(nativePss)}KB ")
        append("pssOther=${median(otherPss)}/${range(otherPss)}KB ")
        append("raw=")
        append(samples.joinToString(prefix = "[", postfix = "]") { sample ->
            "managed=${sample.managedSnapshot[27]} nativeAllocated=${sample.nativeHeapAllocatedBytes} " +
                "nativeHeapSize=${sample.nativeHeapSizeBytes} pss=${sample.totalPssKb}KB " +
                "(dalvik=${sample.dalvikPssKb},native=${sample.nativePssKb},other=${sample.otherPssKb})"
        })
    }

    private fun linearSlope(values: List<Long>): Double {
        val meanX = (values.indices.sum().toDouble()) / values.size
        val meanY = values.average()
        var numerator = 0.0
        var denominator = 0.0
        values.forEachIndexed { index, value ->
            val deltaX = index - meanX
            numerator += deltaX * (value - meanY)
            denominator += deltaX * deltaX
        }
        return if (denominator == 0.0) 0.0 else numerator / denominator
    }
}
