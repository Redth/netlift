-keep class com.netlift.androidhost.MainActivity {
    native <methods>;
}

-keep class com.netlift.androidhost.AndroidLiftNativeAbi {
    native <methods>;
}

-keep interface com.netlift.androidhost.contract.LiftNativeCallbacks {
    public void onRequest(long, long, int, byte[]);
    public void onResult(long, int, byte[], byte[]);
    public void onRelease(long);
}

-keepclassmembers class * implements com.netlift.androidhost.contract.LiftNativeCallbacks {
    public void onRequest(long, long, int, byte[]);
    public void onResult(long, int, byte[], byte[]);
    public void onRelease(long);
}

-keep class com.netlift.androidhost.contract.LiftNativeRuntimeInfo {
    public <init>(int, int, byte[]);
}

-keep class com.netlift.androidhost.contract.LiftAbiException {
    public <init>(int, byte[]);
}
