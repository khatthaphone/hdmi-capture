package com.example.hdmicaptain.record

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer
import kotlin.concurrent.thread

/** Video codec choice (CaptureCore's Recorder.Codec): HEVC where the phone can encode it. */
enum class VideoCodec(val mime: String, val title: String, val bitsPerPixel: Double) {
    Hevc(MediaFormat.MIMETYPE_VIDEO_HEVC, "HEVC (H.265)", 0.15),
    H264(MediaFormat.MIMETYPE_VIDEO_AVC, "H.264", 0.25);

    companion object {
        /** HEVC if a hardware-backed encoder takes this size, else H.264. */
        fun best(width: Int, height: Int, fps: Int): VideoCodec {
            val list = MediaCodecList(MediaCodecList.REGULAR_CODECS)
            val hevc = MediaFormat.createVideoFormat(Hevc.mime, width, height).apply { setInteger(MediaFormat.KEY_FRAME_RATE, fps) }
            return if (list.findEncoderForFormat(hevc) != null) Hevc else H264
        }
    }
}

/** Receives encoded samples. Called on the encoder threads; implementations synchronise. */
interface SampleSink {
    enum class Track { Video, Audio }

    fun onFormat(track: Track, format: MediaFormat)
    fun onSample(track: Track, data: ByteBuffer, info: MediaCodec.BufferInfo)
}

/**
 * Video (MediaCodec fed through [inputSurface] by the renderer) + AAC audio (fed from the card's
 * audio tap) encoders, draining to a [SampleSink]. Timestamps are monotonic microseconds: video
 * from the frame's arrival time (eglPresentationTimeANDROID), audio counted from its first read.
 */
class AvEncoder(
    width: Int,
    height: Int,
    fps: Int,
    codec: VideoCodec,
    keyframeSeconds: Int,
    /** Fills interleaved stereo 48 kHz floats; returns frames read. Null = no audio track. */
    private val audioSource: ((FloatArray) -> Int)?,
    private val sink: SampleSink,
) {
    private val video = MediaCodec.createEncoderByType(codec.mime)
    val inputSurface: Surface
    private val audio = audioSource?.let { MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC) }
    @Volatile private var running = true
    private val threads = mutableListOf<Thread>()

    init {
        val format = MediaFormat.createVideoFormat(codec.mime, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, (width.toDouble() * height * fps * codec.bitsPerPixel).toInt())
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, keyframeSeconds)
            setInteger(MediaFormat.KEY_PRIORITY, 0)  // real time
        }
        video.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        inputSurface = video.createInputSurface()
        video.start()
        threads += thread(name = "encode-video") { drain(video, SampleSink.Track.Video) }

        audio?.let { aac ->
            val audioFormat = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, SAMPLE_RATE, 2).apply {
                setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
                setInteger(MediaFormat.KEY_BIT_RATE, 192_000)
            }
            aac.configure(audioFormat, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            aac.start()
            threads += thread(name = "encode-audio-in") { feedAudio(aac) }
            threads += thread(name = "encode-audio") { drain(aac, SampleSink.Track.Audio) }
        }
    }

    /** Ask for a keyframe now (segment rotation). */
    fun requestKeyframe() = runCatching {
        video.setParameters(android.os.Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
    }

    /** Ends both streams and waits for the last samples; the renderer must have dropped the surface. */
    fun finish() {
        runCatching { video.signalEndOfInputStream() }
        running = false
        threads.forEach { it.join(3_000) }
        listOfNotNull(video, audio).forEach { runCatching { it.stop() }; it.release() }
        inputSurface.release()
    }

    private fun feedAudio(aac: MediaCodec) {
        val source = audioSource ?: return
        val floats = FloatArray(2048 * 2)
        var framesQueued = 0L
        val startUs = System.nanoTime() / 1_000
        while (true) {
            val got = if (running) source(floats) else 0
            if (got == 0) {
                if (!running) break
                Thread.sleep(5)
                continue
            }
            val index = aac.dequeueInputBuffer(10_000)
            if (index < 0) continue
            val buffer = aac.getInputBuffer(index)!!
            buffer.clear()
            val frames = minOf(got, buffer.remaining() / 4)
            for (i in 0 until frames * 2) buffer.putShort((floats[i].coerceIn(-1f, 1f) * 32767f).toInt().toShort())
            aac.queueInputBuffer(index, 0, frames * 4, startUs + framesQueued * 1_000_000 / SAMPLE_RATE, 0)
            framesQueued += frames
        }
        val index = aac.dequeueInputBuffer(100_000)
        if (index >= 0) aac.queueInputBuffer(index, 0, 0, startUs + framesQueued * 1_000_000 / SAMPLE_RATE, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
    }

    private fun drain(codec: MediaCodec, track: SampleSink.Track) {
        val info = MediaCodec.BufferInfo()
        var idleSinceNs = 0L
        while (true) {
            val index = codec.dequeueOutputBuffer(info, 10_000)
            when {
                index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> sink.onFormat(track, codec.outputFormat)
                index >= 0 -> {
                    idleSinceNs = 0
                    val data = codec.getOutputBuffer(index)!!
                    val config = info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
                    if (!config && info.size > 0) {
                        data.position(info.offset)
                        data.limit(info.offset + info.size)
                        sink.onSample(track, data, info)
                    }
                    codec.releaseOutputBuffer(index, false)
                    if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
                }
                !running -> {
                    // Finishing: end-of-stream should come; don't wait forever if it doesn't.
                    if (idleSinceNs == 0L) idleSinceNs = System.nanoTime()
                    else if (System.nanoTime() - idleSinceNs > 1_000_000_000L) return
                }
            }
        }
    }

    companion object {
        const val SAMPLE_RATE = 48_000
        private const val TAG = "HDMICaptain"

        fun log(message: String) = Log.i(TAG, message)
    }
}
