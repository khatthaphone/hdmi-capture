package com.example.hdmicaptain.capture

/**
 * A video source feeding the native frame ring: UVC (libusb + libuvc), Camera2 external, or a test pattern.
 * Frames stay native; this interface only carries control (tasks 071, 072, 077).
 */
interface CaptureSource {
    val name: String
    fun start()
    fun stop()
}
