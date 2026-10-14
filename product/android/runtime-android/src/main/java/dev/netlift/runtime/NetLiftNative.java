package dev.netlift.runtime;

final class NetLiftNative {
    private NetLiftNative() {
    }

    static native void attach(ClassLoader classLoader);

    static native byte[] registerNativeService(
        int serviceId,
        long ownerToken,
        NetLiftNativeServiceDispatcher dispatcher
    );

    static native int revokeNativeService(int serviceId, long ownerToken);

    static native byte[] invoke(
        long operationToken,
        int serviceId,
        int memberId,
        byte[] payload
    );

    static native byte[] openStream(
        long operationToken,
        int serviceId,
        int memberId,
        byte[] payload
    );

    static native byte[] nextStream(long operationToken);

    static native int closeStream(long operationToken);

    static native int cancel(long operationToken);

    static native byte[] invokeOnAttachedThread(
        int serviceId,
        int memberId,
        byte[] payload
    );

    static native int globalReferenceCount();
}
