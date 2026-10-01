package org.netlift.nativebaseline

import android.app.Application
import android.os.SystemClock

class NativeBaselineApplication : Application() {
    var processStartNanos: Long = 0
        private set

    override fun onCreate() {
        super.onCreate()
        processStartNanos = SystemClock.elapsedRealtimeNanos()
    }
}
