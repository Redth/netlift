package com.netlift.androidhost

import android.app.Activity
import android.os.Bundle
import com.netlift.androidhost.contract.Point

class MainActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val generatedPoint = Point(x = 7)
        check(generatedPoint.x == 7)
    }
}
