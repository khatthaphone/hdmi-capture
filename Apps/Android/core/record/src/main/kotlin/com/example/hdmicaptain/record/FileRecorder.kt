package com.example.hdmicaptain.record

import android.content.ContentValues
import android.content.Context
import android.media.MediaCodec
import android.media.MediaFormat
import android.net.Uri
import android.provider.MediaStore
import android.view.Surface
import java.nio.ByteBuffer
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Recording to Movies/HDMI Captain (Pro, spec §12.8): the clean feed + card audio into one MP4,
 * written through MediaStore so it shows up in Photos with no storage permission.
 */
class FileRecorder(
    private val context: Context,
    width: Int,
    height: Int,
    fps: Int,
    audioSource: ((FloatArray) -> Int)?,
    /** Hands the encoder surface to the renderer (null to take it back). */
    private val attach: (Surface?) -> Unit,
) {
    private val uri: Uri
    private val file = context.contentResolver.let { resolver ->
        val values = ContentValues().apply {
            put(MediaStore.Video.Media.DISPLAY_NAME, "HDMI Captain ${stamp()}.mp4")
            put(MediaStore.Video.Media.MIME_TYPE, "video/mp4")
            put(MediaStore.Video.Media.RELATIVE_PATH, "Movies/HDMI Captain")
            put(MediaStore.Video.Media.IS_PENDING, 1)
        }
        uri = resolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, values) ?: error("Couldn't create the video file")
        resolver.openFileDescriptor(uri, "rw") ?: error("Couldn't open the video file")
    }
    private val muxer = Muxer(file.fileDescriptor, expectAudio = audioSource != null)
    private val lock = Any()
    private val encoder = AvEncoder(width, height, fps, VideoCodec.best(width, height, fps), keyframeSeconds = 2, audioSource, object : SampleSink {
        override fun onFormat(track: SampleSink.Track, format: MediaFormat) = synchronized(lock) { muxer.addFormat(track, format) }
        override fun onSample(track: SampleSink.Track, data: ByteBuffer, info: MediaCodec.BufferInfo) = synchronized(lock) { muxer.write(track, data, info) }
    })
    val startedAtNanos = System.nanoTime()

    init {
        attach(encoder.inputSurface)
    }

    /** Stops and publishes the file; null when nothing was recorded (deleted). */
    fun stop(): Uri? {
        attach(null)
        encoder.finish()
        val ok = synchronized(lock) { muxer.finish() }
        file.close()
        val resolver = context.contentResolver
        if (!ok) {
            resolver.delete(uri, null, null)
            return null
        }
        resolver.update(uri, ContentValues().apply { put(MediaStore.Video.Media.IS_PENDING, 0) }, null, null)
        return uri
    }

    companion object {
        internal fun stamp(): String = SimpleDateFormat("yyyy-MM-dd 'at' HH.mm.ss", Locale.US).format(Date())
    }
}
