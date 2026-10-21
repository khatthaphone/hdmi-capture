package com.example.hdmicaptain.record

import android.media.MediaCodec
import android.media.MediaFormat
import android.media.MediaMuxer
import java.io.FileDescriptor
import java.nio.ByteBuffer

/**
 * MediaMuxer that can take samples before it has started: MediaMuxer needs every track before
 * start(), but encoders report formats at different times. Samples wait (copied) until both
 * formats are known, so the first video keyframe is never lost.
 */
internal class Muxer(fd: FileDescriptor, private val expectAudio: Boolean) {
    private val muxer = MediaMuxer(fd, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
    private val tracks = mutableMapOf<SampleSink.Track, Int>()
    private var started = false
    private val waiting = mutableListOf<Triple<SampleSink.Track, ByteArray, MediaCodec.BufferInfo>>()
    var firstVideoUs = -1L
        private set
    var lastVideoUs = -1L
        private set

    fun addFormat(track: SampleSink.Track, format: MediaFormat) {
        if (started || track in tracks) return
        tracks[track] = muxer.addTrack(format)
        if (SampleSink.Track.Video in tracks && (!expectAudio || SampleSink.Track.Audio in tracks)) {
            muxer.start()
            started = true
            waiting.forEach { (t, bytes, info) -> write(t, ByteBuffer.wrap(bytes), info) }
            waiting.clear()
        }
    }

    fun write(track: SampleSink.Track, data: ByteBuffer, info: MediaCodec.BufferInfo) {
        if (!started) {
            if (waiting.size < 600) {
                val bytes = ByteArray(data.remaining()).also { data.duplicate().get(it) }
                waiting += Triple(track, bytes, MediaCodec.BufferInfo().apply { set(0, bytes.size, info.presentationTimeUs, info.flags) })
            }
            return
        }
        val index = tracks[track] ?: return
        muxer.writeSampleData(index, data, info)
        if (track == SampleSink.Track.Video) {
            if (firstVideoUs < 0) firstVideoUs = info.presentationTimeUs
            lastVideoUs = info.presentationTimeUs
        }
    }

    /** True when something was written (a valid file). */
    fun finish(): Boolean {
        val ok = started && firstVideoUs >= 0
        runCatching { if (started) muxer.stop() }
        muxer.release()
        return ok
    }
}
