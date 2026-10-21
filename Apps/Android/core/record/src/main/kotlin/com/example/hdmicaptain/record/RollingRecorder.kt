package com.example.hdmicaptain.record

import android.content.ContentValues
import android.content.Context
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMuxer
import android.net.Uri
import android.provider.MediaStore
import android.view.Surface
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer

/**
 * Save Recent (Pro; CaptureCore's RollingRecorder): keeps the last [keepSeconds] of the input on
 * disk as ~10 s MP4 segments that each start on a keyframe, and [save] stitches the ones covering
 * the requested span into a single file. Memory stays flat however long the buffer is.
 */
class RollingRecorder(
    private val context: Context,
    private val folder: File,
    width: Int,
    height: Int,
    fps: Int,
    private val audioSource: ((FloatArray) -> Int)?,
    private val attach: (Surface?) -> Unit,
    @Volatile var keepSeconds: Int,
    private val segmentSeconds: Int = 10,
) {
    private data class Segment(val file: File, val startUs: Long, val endUs: Long)

    private val lock = Any()
    private val segments = ArrayDeque<Segment>()
    private var current: Pair<File, Muxer>? = null
    private var currentStartUs = -1L
    private var formats = mutableMapOf<SampleSink.Track, MediaFormat>()
    private var counter = 0
    private var rotateRequested = false
    private val encoder: AvEncoder

    init {
        folder.deleteRecursively()
        folder.mkdirs()
        encoder = AvEncoder(width, height, fps, VideoCodec.best(width, height, fps), keyframeSeconds = 2, audioSource, object : SampleSink {
            override fun onFormat(track: SampleSink.Track, format: MediaFormat) = synchronized(lock) {
                formats[track] = format
                current?.second?.addFormat(track, format)
                Unit
            }

            override fun onSample(track: SampleSink.Track, data: ByteBuffer, info: MediaCodec.BufferInfo) = synchronized(lock) { sample(track, data, info) }
        })
        attach(encoder.inputSurface)
    }

    /** Seconds of footage on disk right now. */
    val bufferedSeconds: Double
        get() = synchronized(lock) {
            val first = segments.firstOrNull()?.startUs ?: currentStartUs
            val last = current?.second?.lastVideoUs ?: segments.lastOrNull()?.endUs ?: -1
            if (first < 0 || last < 0) 0.0 else (last - first) / 1e6
        }

    private fun sample(track: SampleSink.Track, data: ByteBuffer, info: MediaCodec.BufferInfo) {
        val keyframe = track == SampleSink.Track.Video && info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
        if (keyframe && (current == null || rotateRequested || info.presentationTimeUs - currentStartUs >= segmentSeconds * 1_000_000L)) {
            rotate(info.presentationTimeUs)
        }
        // Until the first segment starts on a keyframe, there is nothing to write into.
        current?.second?.write(track, data, info)
    }

    private fun rotate(startUs: Long) {
        closeCurrent()
        rotateRequested = false
        val file = File(folder, "segment-${counter++}.mp4")
        val muxer = Muxer(RandomAccessFile(file, "rw").fd, expectAudio = audioSource != null)
        formats.forEach { (track, format) -> muxer.addFormat(track, format) }
        current = file to muxer
        currentStartUs = startUs
        prune()
    }

    private fun closeCurrent() {
        val (file, muxer) = current ?: return
        val end = muxer.lastVideoUs
        if (muxer.finish()) segments.addLast(Segment(file, currentStartUs, end)) else file.delete()
        current = null
    }

    private fun prune() {
        val newest = segments.lastOrNull()?.endUs ?: return
        while (segments.size > 1 && newest - segments.first().endUs > keepSeconds * 1_000_000L) segments.removeFirst().file.delete()
    }

    /**
     * Saves the last [seconds] (null = everything kept) to Movies/HDMI Captain. Closes the running
     * segment so the newest footage is included; the next segment starts at the next keyframe.
     */
    fun save(seconds: Int?): Uri? {
        val chosen = synchronized(lock) {
            closeCurrent()
            rotateRequested = true
            val newest = segments.lastOrNull()?.endUs ?: return null
            val from = seconds?.let { newest - it * 1_000_000L } ?: Long.MIN_VALUE
            segments.filter { it.endUs >= from }.map { it.file.copyTo(File(folder, "save-${it.file.name}"), overwrite = true) to it.startUs }
        }
        encoder.requestKeyframe()
        return try {
            stitch(chosen)
        } finally {
            chosen.forEach { it.first.delete() }
        }
    }

    fun stop() {
        attach(null)
        encoder.finish()
        synchronized(lock) { current?.second?.finish(); current = null }
        folder.deleteRecursively()
    }

    /**
     * Remuxes segment files back to back into one MP4 (no re-encoding). Each segment's samples are
     * relative to its own start (MediaMuxer rebases), so they are placed at the segment's start time.
     */
    private fun stitch(files: List<Pair<File, Long>>): Uri? {
        if (files.isEmpty()) return null
        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.Video.Media.DISPLAY_NAME, "HDMI Captain Recent ${FileRecorder.stamp()}.mp4")
            put(MediaStore.Video.Media.MIME_TYPE, "video/mp4")
            put(MediaStore.Video.Media.RELATIVE_PATH, "Movies/HDMI Captain")
            put(MediaStore.Video.Media.IS_PENDING, 1)
        }
        val uri = resolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, values) ?: return null
        val written = resolver.openFileDescriptor(uri, "rw")?.use { pfd ->
            val muxer = MediaMuxer(pfd.fileDescriptor, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
            var started = false
            val outTracks = mutableMapOf<String, Int>()
            val firstStartUs = files.first().second
            val buffer = ByteBuffer.allocateDirect(4 * 1024 * 1024)
            val info = MediaCodec.BufferInfo()
            for ((file, startUs) in files) {
                val extractor = MediaExtractor().apply { setDataSource(file.absolutePath) }
                val map = mutableMapOf<Int, Int>()
                for (i in 0 until extractor.trackCount) {
                    val format = extractor.getTrackFormat(i)
                    val mime = format.getString(MediaFormat.KEY_MIME) ?: continue
                    if (!started) outTracks.getOrPut(mime) { muxer.addTrack(format) }
                    outTracks[mime]?.let { map[i] = it; extractor.selectTrack(i) }
                }
                if (!started) {
                    muxer.start()
                    started = true
                }
                val offsetUs = startUs - firstStartUs
                var fileBaseUs = -1L
                while (true) {
                    val size = extractor.readSampleData(buffer, 0)
                    if (size < 0) break
                    val track = map[extractor.sampleTrackIndex]
                    if (fileBaseUs < 0) fileBaseUs = extractor.sampleTime
                    if (track != null) {
                        info.set(0, size, offsetUs + maxOf(0L, extractor.sampleTime - fileBaseUs), if (extractor.sampleFlags and MediaExtractor.SAMPLE_FLAG_SYNC != 0) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
                        muxer.writeSampleData(track, buffer, info)
                    }
                    extractor.advance()
                }
                extractor.release()
            }
            runCatching { muxer.stop() }
            muxer.release()
            started
        } ?: false
        if (!written) {
            resolver.delete(uri, null, null)
            return null
        }
        resolver.update(uri, ContentValues().apply { put(MediaStore.Video.Media.IS_PENDING, 0) }, null, null)
        return uri
    }
}
