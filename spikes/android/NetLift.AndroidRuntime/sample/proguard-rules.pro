-keep class com.netlift.androidhost.contract.LiftNativeRuntimeInfo { *; }
-keep class * implements com.netlift.androidruntime.NetLiftNativeCallbacks {
    public void onRequest(long, long, int, byte[]);
    public void onResult(long, int, byte[], byte[]);
    public void onRelease(long);
}
