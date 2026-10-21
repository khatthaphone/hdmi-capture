package com.example.hdmicaptain.model

import kotlin.math.abs

/** A frame-rate range a capture format supports, in frames per second. */
data class FrameRateRange(val min: Double, val max: Double)

/** Framework-free description of a capture format, so selection logic stays pure and testable. */
data class FormatDescriptor(
    val width: Int,
    val height: Int,
    /** Four-character code: UVC names (NV12, YUY2, MJPG) or Apple names (420v, yuvs, dmb1). */
    val fourCC: String,
    val frameRateRanges: List<FrameRateRange>,
) {
    val pixelCount: Int get() = width * height
    val maxFrameRate: Double get() = frameRateRanges.maxOfOrNull { it.max } ?: 0.0
    val isCompressed: Boolean get() = fourCC in COMPRESSED
    /** Exactly 16:9 — what nearly every HDMI source outputs. */
    val isWidescreen: Boolean get() = width * 9 == height * 16

    /** Bytes per uncompressed frame on the wire; null for compressed formats. */
    val bytesPerFrame: Double?
        get() = when {
            isCompressed -> null
            fourCC in BI_PLANAR_420 -> pixelCount * 1.5
            else -> pixelCount * 2.0
        }

    /** The supported rate closest to [target]. */
    fun nearestSupportedRate(target: Double): Double =
        frameRateRanges.map { target.coerceIn(it.min, it.max) }.minByOrNull { abs(it - target) } ?: 0.0

    /** The same format limited to the rates [link] can carry (the whole list when [link] is null). */
    fun limitedTo(link: UsbLink?): FormatDescriptor {
        val bytes = bytesPerFrame ?: return this
        if (link == null) return this
        val maxRate = link.bytesPerSecond / bytes
        return copy(frameRateRanges = frameRateRanges.filter { it.min <= maxRate }.map { it.copy(max = minOf(it.max, maxRate)) })
    }

    companion object {
        val COMPRESSED = setOf("dmb1", "jpeg", "mjpa", "mjpg", "avc1", "hvc1", "MJPG", "H264", "H265")
        val BI_PLANAR_420 = setOf("420v", "420f", "NV12", "NV21", "I420")
    }
}

/**
 * Usable bandwidth of the card's USB connection. Android only: a phone's USB 2.0 port can't carry
 * uncompressed 1080p. Budgets are estimates until spike 072 measures them.
 */
enum class UsbLink(val bytesPerSecond: Double) {
    /** High-speed, high-bandwidth isochronous: 3 × 1024 B per 125 µs microframe. */
    Usb2(24_000_000.0),
    Usb3(300_000_000.0),
}

sealed interface FormatPreference {
    /** Smooth frame rate first, then 16:9, then highest resolution. */
    data object Auto : FormatPreference

    /** A specific resolution, optionally at a specific frame rate. */
    data class Pinned(val width: Int, val height: Int, val frameRate: Double?) : FormatPreference
}

data class FormatChoice(
    /** Index into the device's format list. */
    val index: Int,
    /** The format as the link allows it (rates clipped to the USB budget). */
    val format: FormatDescriptor,
    val frameRate: Double,
)

/** Port of CaptureCore's `FormatSelector`, plus the USB link limit. Shared fixtures: docs/fixtures/. */
object FormatSelector {
    /** Formats at or above this rate are treated as "smooth" and win over higher resolutions below it. */
    const val SMOOTH_FRAME_RATE = 49.9

    fun select(
        formats: List<FormatDescriptor>,
        preference: FormatPreference = FormatPreference.Auto,
        link: UsbLink? = null,
    ): FormatChoice? {
        val limited = formats.map { it.limitedTo(link) }
        val usable = limited.indices.filter { limited[it].maxFrameRate > 0 }
        if (usable.isEmpty()) return null

        if (preference is FormatPreference.Pinned) {
            val matches = usable.filter { limited[it].width == preference.width && limited[it].height == preference.height }
            if (matches.isNotEmpty()) return pinned(matches, limited, preference.frameRate)
        }
        val best = usable.maxWith(compareByRank(limited) { autoRank(it) })
        return FormatChoice(best, limited[best], limited[best].maxFrameRate)
    }

    /** Distinct resolution/frame-rate combinations, best first, for a settings picker. */
    fun options(formats: List<FormatDescriptor>, link: UsbLink? = null): List<FormatPreference.Pinned> {
        val seen = mutableSetOf<String>()
        return formats.map { it.limitedTo(link) }
            .filter { it.maxFrameRate > 0 }
            .sortedWith(compareByDescending<FormatDescriptor> { it.pixelCount }.thenByDescending { it.maxFrameRate })
            .mapNotNull { f ->
                val rate = Math.round(f.maxFrameRate * 100) / 100.0
                if (seen.add("${f.width}x${f.height}@$rate")) FormatPreference.Pinned(f.width, f.height, rate) else null
            }
    }

    private fun pinned(matches: List<Int>, formats: List<FormatDescriptor>, frameRate: Double?): FormatChoice {
        if (frameRate == null) {
            val best = matches.maxWith(compareByRank(formats) { autoRank(it) })
            return FormatChoice(best, formats[best], formats[best].maxFrameRate)
        }
        // Nearest achievable rate first, then prefer uncompressed.
        val best = matches.maxWith(compareByRank(formats) {
            listOf(-abs(it.nearestSupportedRate(frameRate) - frameRate), encodingScore(it))
        })
        return FormatChoice(best, formats[best], formats[best].nearestSupportedRate(frameRate))
    }

    private fun autoRank(f: FormatDescriptor): List<Double> = listOf(
        if (f.maxFrameRate >= SMOOTH_FRAME_RATE) 1.0 else 0.0,
        if (f.isWidescreen) 1.0 else 0.0,
        f.pixelCount.toDouble(),
        f.maxFrameRate,
        encodingScore(f),
    )

    /** Uncompressed YCbCr avoids an MJPEG decode step (latency + CPU). */
    private fun encodingScore(f: FormatDescriptor): Double = when {
        f.fourCC in FormatDescriptor.BI_PLANAR_420 -> 3.0
        f.fourCC in setOf("yuvs", "2vuy", "yuv2", "YUY2", "UYVY") -> 2.0
        f.isCompressed -> 0.0
        else -> 1.0
    }

    /** Lexicographic order over a rank list; ties keep the earlier index, like Swift's `max(by:)`. */
    private fun compareByRank(formats: List<FormatDescriptor>, rank: (FormatDescriptor) -> List<Double>) =
        Comparator<Int> { a, b ->
            rank(formats[a]).zip(rank(formats[b])).firstOrNull { it.first != it.second }
                ?.let { it.first.compareTo(it.second) } ?: 0
        }
}
