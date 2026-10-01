package com.netlift.androidruntime

public object NetLiftNativeFailureHooks {
    public external fun failNextV2Handoff(
        serviceId: Int,
        callbacks: NetLiftNativeCallbacksV2,
        failAbort: Boolean
    ): LongArray?

    public external fun armCallCleanupBarrier(kind: Int)

    public external fun awaitCallCleanupBarrier(kind: Int, timeoutMillis: Long): Boolean

    public external fun releaseCallCleanupBarrier()

    public external fun failNextReleaseEnvironment()

    public external fun drainPendingReleases(): Int

    public external fun ownershipSnapshot(): LongArray?

    public external fun managedRetentionSnapshot(): LongArray?
}
