package com.example.hdmicaptain.audio

import android.content.Context
import android.media.AudioDeviceInfo
import android.media.AudioManager
import com.example.hdmicaptain.model.AudioSettings

/**
 * HDMI audio passthrough (spec §12.6): the card's USB audio input → speakers or headphones, with
 * delay, channel fixes, volume and mute. Native (Oboe) callbacks do the work; this class only
 * picks the device and passes settings. Not thread-safe: call from the capture control thread.
 */
class CardAudio(context: Context) {
    data class Levels(
        val peakLeft: Float,
        val peakRight: Float,
        val rmsLeft: Float,
        val rmsRight: Float,
        val bufferedMs: Float,
        val underruns: Long,
        val running: Boolean,
    )

    class AudioException(message: String) : Exception(message)

    private val audioManager = context.getSystemService(AudioManager::class.java)
    private var handle = nativeCreate()

    /** The card's USB input: the one whose product name matches the card, else the only USB input. */
    fun findInput(cardName: String?): AudioDeviceInfo? {
        val usbInputs = audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS)
            .filter { it.type == AudioDeviceInfo.TYPE_USB_DEVICE || it.type == AudioDeviceInfo.TYPE_USB_HEADSET }
        return usbInputs.firstOrNull { cardName != null && it.productName?.toString() == cardName }
            ?: usbInputs.singleOrNull()
    }

    fun start(input: AudioDeviceInfo) {
        val error = nativeStart(handle, input.id)
        if (error.isNotEmpty()) throw AudioException(error)
    }

    fun stop() = nativeStop(handle)

    fun apply(settings: AudioSettings) =
        nativeSettings(handle, settings.delayMs.coerceIn(0, 500), settings.channelMode.ordinal, settings.volume, settings.muted)

    /** Recording tap on/off; when on, [readTap] returns captured 48 kHz stereo frames. */
    fun setTap(on: Boolean) = nativeSetTap(handle, on)

    /** Fills [out] (interleaved stereo) with captured frames; returns how many frames. */
    fun readTap(out: FloatArray): Int = nativeReadTap(handle, out)

    fun levels(): Levels {
        val v = FloatArray(7)
        nativeLevels(handle, v)
        return Levels(v[0], v[1], v[2], v[3], v[4], v[5].toLong(), v[6] > 0)
    }

    fun release() {
        if (handle != 0L) nativeDestroy(handle)
        handle = 0L
    }

    private companion object {
        init {
            System.loadLibrary("hdmiaudio")
        }

        @JvmStatic external fun nativeCreate(): Long
        @JvmStatic external fun nativeDestroy(handle: Long)
        @JvmStatic external fun nativeStart(handle: Long, inputDeviceId: Int): String
        @JvmStatic external fun nativeStop(handle: Long)
        @JvmStatic external fun nativeSettings(handle: Long, delayMs: Int, channelMode: Int, volume: Float, muted: Boolean)
        @JvmStatic external fun nativeLevels(handle: Long, out: FloatArray)
        @JvmStatic external fun nativeSetTap(handle: Long, on: Boolean)
        @JvmStatic external fun nativeReadTap(handle: Long, out: FloatArray): Int
    }
}
