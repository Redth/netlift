-keep class com.netlift.androidruntime.NetLiftHost { *; }
-keep class net.dot.android.crypto.DotnetProxyTrustManager { *; }
-keep class net.dot.android.crypto.DotnetX509KeyManager { *; }
-keep class net.dot.android.crypto.PalPbkdf2 { *; }
-keep class com.netlift.androidruntime.v5.NativeV5Api { *; }
-keep class com.netlift.androidruntime.v5.NetLiftV5Endpoint { *; }
-keep class com.netlift.androidruntime.v5.NetLiftV5Endpoint$Companion { *; }
-keep class com.netlift.androidruntime.v5.NetLiftV5AsyncSession { *; }
-keep class com.netlift.androidruntime.v5.ManagedResultSchemaContext { *; }
-keep class com.netlift.androidruntime.v5.NegotiatedPeerContext { *; }
-keep class com.netlift.androidruntime.v5.NegotiatedPeerField { *; }
-keep enum com.netlift.androidruntime.v5.NegotiatedPeerFieldKind { *; }
-keep interface com.netlift.androidruntime.NetLiftNativeCallbacks { *; }
-keep interface com.netlift.androidruntime.NetLiftNativeCallbacksV2 { *; }
-keep class com.netlift.androidruntime.NativeAbiException { *; }
-keep interface com.netlift.androidruntime.NetLiftStreamCallbacks { *; }
-keep class * implements com.netlift.androidruntime.NetLiftStreamCallbacks {
    public void onNext(long, byte[]);
    public void onTerminal(long, int, byte[]);
    public void onRelease(long);
}
-keep class * implements com.netlift.androidruntime.NetLiftNativeCallbacksV2 {
    public void onRequest(long, long, int, byte[]);
    public void onResult(long, int, byte[], byte[]);
    public void onRelease(long);
    public void onRevoke(long, long);
}
