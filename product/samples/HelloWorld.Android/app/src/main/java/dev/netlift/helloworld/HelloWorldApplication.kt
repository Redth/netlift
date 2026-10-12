package dev.netlift.helloworld

import android.app.Application
import dev.netlift.runtime.NetLiftRuntime

class HelloWorldApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        NetLiftRuntime.initialize(this)
    }
}
