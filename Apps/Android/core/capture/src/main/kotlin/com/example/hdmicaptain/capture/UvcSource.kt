package com.example.hdmicaptain.capture

import android.hardware.usb.UsbDeviceConnection
import com.example.hdmicaptain.model.FormatChoice
import com.example.hdmicaptain.model.FormatDescriptor
import com.example.hdmicaptain.model.FrameRateRange
import com.example.hdmicaptain.model.UsbLink
import org.json.JSONObject
import kotlin.math.roundToInt

/**
 * A UVC capture card driven by libusb + libuvc on the fd Android's UsbManager opened. Frames go
 * from libuvc's stream thread straight into the renderer's frame ring; Kotlin only controls.
 * Not thread-safe: call from the capture control thread.
 */
class UvcSource {
    /** What the card offers, parsed from its descriptors. */
    data class Description(val link: UsbLink?, val speed: String, val formats: List<FormatDescriptor>, val json: String)

    data class Stats(
        val frames: Long,
        val shortFrames: Long,
        val compressedFrames: Long,
        val lastFrameNanos: Long,
        /** MJPEG decode (task 074). */
        val decoded: Long,
        val decodeDropped: Long,
        val decodeFailed: Long,
        val avgDecodeNanos: Long,
    )

    class UvcException(message: String) : Exception(message)

    private var handle = nativeCreate()
    private var connection: UsbDeviceConnection? = null

    fun open(connection: UsbDeviceConnection) {
        close()
        check(nativeOpen(handle, connection.fileDescriptor))
        this.connection = connection
    }

    fun describe(): Description {
        val json = nativeDescribe(handle)
        val root = JSONObject(json)
        val speed = root.optString("speed", "unknown")
        val link = when (speed) {
            "usb2", "usb1", "usb1-low" -> UsbLink.Usb2
            "usb3", "usb3-plus" -> UsbLink.Usb3
            else -> null
        }
        val array = root.optJSONArray("formats")
        val formats = (0 until (array?.length() ?: 0)).map { i ->
            val f = array!!.getJSONObject(i)
            val ranges = f.optJSONArray("rates")?.let { r -> (0 until r.length()).map { FrameRateRange(r.getDouble(it), r.getDouble(it)) } }
                ?: f.getJSONArray("ranges").getJSONArray(0).let { listOf(FrameRateRange(it.getDouble(0), it.getDouble(1))) }
            FormatDescriptor(f.getInt("w"), f.getInt("h"), f.getString("cc"), ranges)
        }
        return Description(link, speed, formats, json)
    }

    /** Starts streaming [choice] into the frame ring of a VideoRenderer ([ringHandle]). */
    fun start(choice: FormatChoice, ringHandle: Long) =
        check(nativeStart(handle, choice.format.fourCC, choice.format.width, choice.format.height, choice.frameRate.roundToInt(), ringHandle))

    fun stop() = nativeStop(handle)

    fun close() {
        nativeClose(handle)
        connection?.close()
        connection = null
    }

    fun stats(): Stats {
        val v = LongArray(8)
        nativeStats(handle, v)
        return Stats(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7])
    }

    fun release() {
        close()
        if (handle != 0L) nativeDestroy(handle)
        handle = 0L
    }

    private fun check(error: String) {
        if (error.isNotEmpty()) throw UvcException(error)
    }

    private companion object {
        init {
            System.loadLibrary("hdmicapture")
        }

        @JvmStatic external fun nativeCreate(): Long
        @JvmStatic external fun nativeDestroy(handle: Long)
        @JvmStatic external fun nativeOpen(handle: Long, fd: Int): String
        @JvmStatic external fun nativeDescribe(handle: Long): String
        @JvmStatic external fun nativeStart(handle: Long, fourCC: String, width: Int, height: Int, fps: Int, ring: Long): String
        @JvmStatic external fun nativeStop(handle: Long)
        @JvmStatic external fun nativeClose(handle: Long)
        @JvmStatic external fun nativeStats(handle: Long, out: LongArray)
    }
}
