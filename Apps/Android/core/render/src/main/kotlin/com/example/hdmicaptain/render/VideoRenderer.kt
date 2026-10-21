package com.example.hdmicaptain.render

import android.graphics.Bitmap
import android.view.Surface
import com.example.hdmicaptain.model.CubeLUT
import com.example.hdmicaptain.model.ImageAdjustments
import com.example.hdmicaptain.model.RenderOptions
import com.example.hdmicaptain.model.SignalAnalyzer
import com.example.hdmicaptain.model.TestPattern
import java.nio.ByteBuffer

/**
 * The native video renderer (`libhdmirender.so`): a render thread with its own EGL context that
 * draws each frame from the capture source as it arrives. Frames never pass through Kotlin.
 * One per viewer; call [release] when the viewer goes away.
 */
class VideoRenderer {
    data class Stats(val framesIn: Long, val framesDrawn: Long, val latencyNanos: Long, val width: Int, val height: Int)

    private var handle: Long = nativeCreate()

    /** The SurfaceView's surface, or null when it is destroyed (blocks until EGL let go of it). */
    fun setSurface(surface: Surface?) = nativeSetSurface(handle, surface)

    fun setOptions(options: RenderOptions) =
        nativeSetOptions(handle, options.scale.ordinal, options.filter.ordinal, options.colorRange.ordinal)

    fun startTestPattern(pattern: TestPattern, width: Int = 1920, height: Int = 1080, fps: Int = 60) =
        nativeStartTestPattern(handle, pattern.ordinal, width, height, fps)

    fun stopSource() = nativeStopSource(handle)

    /** Picture look (adjustments + optional LUT at [lutIntensity]), applied on screen. */
    fun setLook(adjustments: ImageAdjustments, lut: CubeLUT?, lutIntensity: Double) {
        val a = floatArrayOf(
            adjustments.brightness.toFloat(), adjustments.contrast.toFloat(), adjustments.saturation.toFloat(),
            adjustments.gamma.toFloat(), adjustments.sharpness.toFloat(),
        )
        nativeSetLook(handle, a, lut?.entries, lut?.size ?: 0, lutIntensity.toFloat(), lut?.let { it.domainMin + it.domainMax })
    }

    /** The latest Y/Cb/Cr sample (every 10th frame) for no-signal detection; [count] grows with each. */
    data class SignalSample(val stats: SignalAnalyzer.Stats, val fullRange: Boolean, val count: Long)

    fun signal(): SignalSample {
        val v = DoubleArray(7)
        nativeSignal(handle, v)
        return SignalSample(
            SignalAnalyzer.Stats(meanY = v[0], meanCb = v[1], meanCr = v[2], rangeY = v[3], rangeChroma = v[4]),
            fullRange = v[5] > 0, count = v[6].toLong(),
        )
    }

    /** Native frame ring for a capture source (UvcSource); valid until [release]. */
    val ringHandle: Long get() = nativeRing(handle)

    fun stats(): Stats {
        val v = LongArray(5)
        nativeStats(handle, v)
        return Stats(v[0], v[1], v[2], v[3].toInt(), v[4].toInt())
    }

    /** The current frame at its own size, without any UI (spec §12.8); null before the first frame. */
    fun screenshot(): Bitmap? {
        val size = IntArray(2)
        val rgba = nativeScreenshot(handle, size) ?: return null
        return Bitmap.createBitmap(size[0], size[1], Bitmap.Config.ARGB_8888).apply {
            copyPixelsFromBuffer(ByteBuffer.wrap(rgba))
        }
    }

    fun release() {
        if (handle != 0L) nativeDestroy(handle)
        handle = 0L
    }

    private companion object {
        init {
            System.loadLibrary("hdmirender")
        }

        @JvmStatic external fun nativeCreate(): Long
        @JvmStatic external fun nativeDestroy(handle: Long)
        @JvmStatic external fun nativeSetSurface(handle: Long, surface: Surface?)
        @JvmStatic external fun nativeSetOptions(handle: Long, scale: Int, filter: Int, range: Int)
        @JvmStatic external fun nativeStartTestPattern(handle: Long, pattern: Int, width: Int, height: Int, fps: Int)
        @JvmStatic external fun nativeStopSource(handle: Long)
        @JvmStatic external fun nativeRing(handle: Long): Long
        @JvmStatic external fun nativeSetLook(handle: Long, adjust: FloatArray, lut: FloatArray?, lutSize: Int, intensity: Float, domain: FloatArray?)
        @JvmStatic external fun nativeSignal(handle: Long, out: DoubleArray)
        @JvmStatic external fun nativeStats(handle: Long, out: LongArray)
        @JvmStatic external fun nativeScreenshot(handle: Long, size: IntArray): ByteArray?
    }
}
