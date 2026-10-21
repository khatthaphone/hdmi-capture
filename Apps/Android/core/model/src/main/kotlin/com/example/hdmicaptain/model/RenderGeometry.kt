package com.example.hdmicaptain.model

import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

data class Rect(val x: Double, val y: Double, val width: Double, val height: Double)

/**
 * Port of CaptureCore's RenderGeometry (the native renderer has the same code): where the picture
 * goes in the drawable (pixels, top-left origin) and which part of the source is visible.
 * Shared fixture: docs/fixtures/render-geometry.json. The guides overlay uses it to sit on the picture.
 */
data class RenderGeometry(val viewport: Rect, val sourceRect: Rect) {
    companion object {
        fun compute(sourceWidth: Double, sourceHeight: Double, width: Double, height: Double, mode: ScaleMode): RenderGeometry {
            val full = Rect(0.0, 0.0, width, height)
            val unit = Rect(0.0, 0.0, 1.0, 1.0)
            if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0 || mode == ScaleMode.Stretch) {
                return RenderGeometry(full, unit)
            }
            val sx = width / sourceWidth
            val sy = height / sourceHeight
            val scale = when (mode) {
                ScaleMode.Fill -> max(sx, sy)
                ScaleMode.Integer -> floor(min(sx, sy)).let { if (it >= 1) it else min(sx, sy) }
                else -> min(sx, sy)
            }
            // Whole pixels, so float error can't push the origin to -1 and crop a column.
            val w = (sourceWidth * scale).roundToInt().toDouble()
            val h = (sourceHeight * scale).roundToInt().toDouble()
            val rect = Rect(floor((width - w) / 2), floor((height - h) / 2), w, h)
            val x0 = max(rect.x, 0.0)
            val y0 = max(rect.y, 0.0)
            val x1 = min(rect.x + w, width)
            val y1 = min(rect.y + h, height)
            return RenderGeometry(
                Rect(x0, y0, x1 - x0, y1 - y0),
                Rect((x0 - rect.x) / w, (y0 - rect.y) / h, (x1 - x0) / w, (y1 - y0) / h),
            )
        }
    }
}

/** Framing overlays drawn above the video (not into it), as CaptureCore's GuideSettings. */
@kotlinx.serialization.Serializable
data class GuideSettings(
    val thirds: Boolean = false,
    val center: Boolean = false,
    val safeAreas: Boolean = false,
    val mask: Mask? = null,
    val opacity: Double = 0.6,
) {
    @kotlinx.serialization.Serializable
    enum class Mask(val title: String, val ratio: Double) {
        Scope("2.39:1", 2.39), Flat("1.85:1", 1.85), Classic("4:3", 4.0 / 3.0), Vertical("9:16", 9.0 / 16.0), Square("1:1", 1.0)
    }

    val isEmpty: Boolean get() = !thirds && !center && !safeAreas && mask == null

    companion object {
        /** The visible region for a mask inside a video rect (letterbox or pillarbox). */
        fun maskRect(mask: Mask, video: Rect): Rect {
            val videoRatio = video.width / video.height
            if (mask.ratio >= videoRatio) {
                val h = video.width / mask.ratio
                return Rect(video.x, video.y + (video.height - h) / 2, video.width, h)
            }
            val w = video.height * mask.ratio
            return Rect(video.x + (video.width - w) / 2, video.y, w, video.height)
        }
    }
}
