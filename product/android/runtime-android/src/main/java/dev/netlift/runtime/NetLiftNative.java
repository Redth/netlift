package dev.netlift.runtime;

final class NetLiftNative {
    private NetLiftNative() {
    }

    static native void attach(ClassLoader classLoader);

    static native byte[] invoke(
        long operationToken,
        int serviceId,
        int memberId,
        byte[] payload
    );

    static native int cancel(long operationToken);

    static native byte[] openStream(
        int serviceId,
        int memberId,
        byte[] payload
    );

    static native byte[] readStream(
        long token,
        long generation
    );

    static native int revokeStream(
        long token,
        long generation
    );

    static native long[] registerNativeService(
        int serviceId,
        Object peer
    );

    static native int revokeNativeService(
        long token,
        long generation
    );

    static native byte[] invokeOnAttachedThread(
        long operationToken,
        int serviceId,
        int memberId,
        byte[] payload
    );

    static native int globalReferenceCount();
}
