package dev.netlift.runtime;

final class NetLiftNative {
    private NetLiftNative() {
    }

    static native void attach(ClassLoader classLoader);

    static native byte[] invoke(int serviceId, int memberId, byte[] payload);

    static native byte[] invokeOnAttachedThread(
        int serviceId,
        int memberId,
        byte[] payload
    );

    static native int globalReferenceCount();
}
