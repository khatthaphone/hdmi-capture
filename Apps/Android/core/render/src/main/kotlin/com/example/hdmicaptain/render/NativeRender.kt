package com.example.hdmicaptain.render

/** Entry point to the native renderer (`libhdmirender.so`). */
object NativeRender {
    init {
        System.loadLibrary("hdmirender")
    }

    val version: String get() = nativeVersion()

    @JvmStatic private external fun nativeVersion(): String
}
